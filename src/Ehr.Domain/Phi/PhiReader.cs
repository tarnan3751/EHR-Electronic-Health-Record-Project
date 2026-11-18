using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Messaging;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Ehr.Data.Proxies;
using Ehr.Data.Scheduling;
using Ehr.Domain.Audit;
using Ehr.Domain.Messaging;
using Ehr.Domain.Orders;
using Ehr.Domain.Proxies;
using Ehr.Domain.Results;
using Ehr.Domain.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Phi;

// The one way to read patient information (AGENTS.md). Every read records an audit event for each patient whose
// data it returned, in the same transaction, so a view without its record is impossible. Row-level security decides
// which rows exist for the current user: a clinician sees every patient, a patient only themselves, anyone else
// nothing. Ehr.Architecture.Tests fails if anything outside this namespace touches the patient entity or the rest of
// the chart.
public sealed class PhiReader(EhrDbContext db, AuditRecorder audit, UserContext user)
{
    public const string PatientsTable = "patients";

    public async Task<Patient?> PatientAsync(Guid id, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is not null)
        {
            audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return patient;
    }

    // A patient's chart: the demographics, the allergies, medications and diagnoses they have now, and their latest
    // visits, up to `visits`, newest first. It is one view of one patient, so it is one recorded read of the patient,
    // however many rows the sections hold: the same event a list of patients records for each. Draft and signed-note
    // reads have their own table/row events, also accepted by the cloud audit queue (CloudAuditEvents).
    public async Task<PatientChart?> ChartAsync(
        Guid id, int visits, CancellationToken cancellationToken, Guid? discontinued = null, bool sensitive = false)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var allergies = await CurrentAllergies(id).ToListAsync(cancellationToken);
        // Sensitive medications and diagnoses are read either way, and left out below unless asked for: the chart
        // says how many it left out, without another query.
        var medications = await db.MedicationStatements.AsNoTracking()
            .Where(m => m.PatientId == id && m.Status == MedicationStatementStatus.Active)
            .OrderBy(m => m.Display).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        var conditions = await db.Conditions.AsNoTracking()
            .Where(c => c.PatientId == id && ConditionClinicalStatus.Current.Contains(c.ClinicalStatus))
            .OrderBy(c => c.OnsetDate == null).ThenByDescending(c => c.OnsetDate).ThenBy(c => c.Display).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var encounters = await db.Encounters.AsNoTracking()
            .Where(e => e.PatientId == id && !EncounterStatus.NotHeld.Contains(e.Status))
            .OrderByDescending(e => e.PeriodStart).ThenBy(e => e.Id)
            .Take(visits)
            .ToListAsync(cancellationToken);
        var orders = await ActiveOrders(id, catalogCode: null).ToListAsync(cancellationToken);
        // Only when the discontinue page sent the clinician here: whether that order is this patient's, and discontinued.
        var confirmed = discontinued is not null && await db.Orders.AsNoTracking()
            .AnyAsync(order => order.Id == discontinued && order.PatientId == id && order.Status == OrderStatus.Revoked, cancellationToken);

        var hidden = 0;
        if (!sensitive)
        {
            hidden = medications.RemoveAll(m => m.Sensitive) + conditions.RemoveAll(c => c.Sensitive);
        }

        // One event for one view: a read, or a read that showed sensitive items.
        audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);

        await db.SaveChangesAsync(cancellationToken);
        var soon = DateTimeOffset.UtcNow.AddHours(-1);
        var upcoming = await AppointmentEntriesAsync(
            db.Appointments.Where(a => a.PatientId == id && a.Status == AppointmentStatus.Booked && a.StartsAt >= soon).OrderBy(a => a.StartsAt).Take(5),
            latestFirst: false, cancellationToken);
        var threads = await db.MessageThreads.AsNoTracking().Where(t => t.PatientId == id)
            .OrderByDescending(t => t.LastMessageAt).ThenBy(t => t.Id).Take(5)
            .Select(t => new ThreadEntry(t.Id, t.Subject, t.LastMessageAt,
                db.Messages.Count(m => m.ThreadId == t.Id && m.SenderKind == MessageSender.Patient && m.ReadAt == null)))
            .ToListAsync(cancellationToken);
        var resulted = await ResultedOrdersAsync(id, 8, cancellationToken);
        return new PatientChart(patient, allergies, medications, conditions, encounters, orders)
        {
            Resulted = resulted,
            Threads = threads,
            Upcoming = upcoming,
            ConfirmsDiscontinued = confirmed,
            SensitiveHidden = hidden,
            SensitiveShown = sensitive,
        };
    }

    // A patient and their current allergies, for the banner of a patient page that isn't the chart (a note, dictation):
    // one recorded read of the patient, as PatientAsync is.
    public async Task<PatientHeader?> HeaderAsync(Guid id, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var allergies = await CurrentAllergies(id).ToListAsync(cancellationToken);
        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientHeader(patient, allergies);
    }

    // The form for one entry of a chart list: the patient, their allergies for the banner and, when changing an entry,
    // that entry, which must be this patient's. One recorded read of the patient: of sensitive items, when the entry
    // is sensitive, since the form shows it.
    public async Task<ChartEntryForm?> ChartEntryAsync(Guid id, string? table, Guid? entry, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var form = new ChartEntryForm(patient, await CurrentAllergies(id).ToListAsync(cancellationToken));
        if (entry is not null)
        {
            form = table switch
            {
                "allergy_intolerances" => form with { Allergy = await db.AllergyIntolerances.AsNoTracking().SingleOrDefaultAsync(a => a.Id == entry && a.PatientId == id, cancellationToken) },
                "medication_statements" => form with { Medication = await db.MedicationStatements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == entry && m.PatientId == id, cancellationToken) },
                "conditions" => form with { Condition = await db.Conditions.AsNoTracking().SingleOrDefaultAsync(c => c.Id == entry && c.PatientId == id, cancellationToken) },
                _ => form,
            };
        }

        var showsSensitive = form.Medication?.Sensitive == true || form.Condition?.Sensitive == true;
        audit.Record(showsSensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);

        await db.SaveChangesAsync(cancellationToken);
        return form;
    }

    // The history of changes to a patient's allergies, medications and diagnoses, newest first, with who made each.
    // Changes to sensitive entries are left out unless asked for, which is recorded, as on the chart.
    public async Task<ChartHistory?> ChartHistoryAsync(Guid id, int limit, bool sensitive, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        // An entry that is sensitive now is sensitive throughout its history, whatever it was when a change was made:
        // a medication added unmarked and marked later doesn't stay readable here. So the entries are asked about, not
        // the old rows, and their changes are left out in the query itself, before the limit.
        List<Guid> sensitiveEntries =
        [
            .. await db.Conditions.AsNoTracking().Where(c => c.PatientId == id && c.Sensitive).Select(c => c.Id).ToListAsync(cancellationToken),
            .. await db.MedicationStatements.AsNoTracking().Where(m => m.PatientId == id && m.Sensitive).Select(m => m.Id).ToListAsync(cancellationToken),
        ];
        var history = db.ChartHistory.AsNoTracking().Where(change => change.PatientId == id);
        var hidden = sensitive || sensitiveEntries.Count == 0
            ? 0
            : await history.CountAsync(change => sensitiveEntries.Contains(change.RowId), cancellationToken);
        var changes = await (
            from change in history
            join staff in db.StaffUsers on change.ChangedById equals staff.Id into changers
            from staff in changers.DefaultIfEmpty()
            where sensitive || !sensitiveEntries.Contains(change.RowId)
            orderby change.ChangedAt descending, change.Id descending
            select new ChartChange(change.TableName, change.RowId, change.ChangedAt, staff == null ? null : staff.DisplayName, change.Before, change.After))
            .Take(limit + 1).ToListAsync(cancellationToken);
        var more = changes.Count > limit;
        changes = [.. changes.Take(limit)];

        // One event for one view: a read, or a read that showed sensitive items.
        audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new ChartHistory(patient, changes, more, hidden, sensitive);
    }

    // UC 1 (Tahseen Arnan): the order form's view of a patient (OrderForm). One view of one patient, so one recorded read
    // of the patient, as the chart's is, whatever orders the duplicate check finds. For a change, `replaces` is the
    // order being replaced: it comes back when it is an active order of this patient, and isn't counted as a duplicate.
    public async Task<OrderForm?> OrderFormAsync(Guid id, string? catalogCode, Guid? replaces, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var allergies = await CurrentAllergies(id).ToListAsync(cancellationToken);
        var replaced = replaces is null ? null : await ActiveOrders(id, catalogCode: null, only: replaces).SingleOrDefaultAsync(cancellationToken);
        List<ActiveOrder> duplicates = catalogCode is null
            ? []
            : await ActiveOrders(id, catalogCode, except: replaces).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new OrderForm(patient, allergies, duplicates, replaced);
    }

    // UC 1: a patient's discontinued orders, newest first, each with who discontinued it, when and why, and the order
    // that replaced it when it was changed. One view of one patient: one recorded read of the patient.
    public async Task<DiscontinuedOrders?> DiscontinuedOrdersAsync(Guid id, int limit, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var orders = await (
            from order in db.Orders.AsNoTracking()
            join item in db.OrderCatalogItems on order.CatalogCode equals item.Code
            join staff in db.StaffUsers on order.DiscontinuerId equals staff.Id
            where order.PatientId == id && order.Status == OrderStatus.Revoked
            orderby order.DiscontinuedAt descending, order.Id
            select new DiscontinuedOrder(order.Id, order.Type, item.Display, order.Details, order.SignedAt, order.DiscontinuedAt!.Value,
                staff.DisplayName, order.DiscontinueReason!, order.ReplacedByOrderId != null))
            .Take(limit + 1).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new DiscontinuedOrders(patient, [.. orders.Take(limit)], orders.Count > limit);
    }

    // The allergies a patient has now, the high-risk ones first.
    IQueryable<AllergyIntolerance> CurrentAllergies(Guid id) =>
        db.AllergyIntolerances.AsNoTracking()
            .Where(a => a.PatientId == id && a.ClinicalStatus == AllergyClinicalStatus.Active)
            .OrderBy(a => a.Criticality == AllergyCriticality.High ? 0 : 1).ThenBy(a => a.Display).ThenBy(a => a.Id);

    // A patient's active orders, of one catalog item or of all, newest first, with only the fields that are shown:
    // just the one order, or all but one, when asked.
    IQueryable<ActiveOrder> ActiveOrders(Guid id, string? catalogCode, Guid? only = null, Guid? except = null) =>
        from order in db.Orders.AsNoTracking()
        join item in db.OrderCatalogItems on order.CatalogCode equals item.Code
        where order.PatientId == id && order.Status == OrderStatus.Active && (catalogCode == null || order.CatalogCode == catalogCode)
            && (only == null || order.Id == only) && (except == null || order.Id != except)
        orderby order.SignedAt descending, order.Id
        select new ActiveOrder(order.Id, order.Type, item.Display, order.Details, order.SignedAt);

    // A patient's released test results for the portal (UC6, FR-16; Simar Rekhi), newest first. Row-level security
    // returns only the signed-in patient's own results, and only released, finished ones (db/policies/30-rls.sql); the
    // same conditions are asked here too, so a clinician's session can't show a patient's unreleased result through
    // this method either. One view of one patient, so one recorded read of the patient, as for the chart: the only
    // kind of read the cloud app's audit queue takes (CloudAuditEvents), so the page works off-site too.
    public async Task<PatientResults?> ResultsAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var results = await db.Observations.AsNoTracking()
            .Where(o => o.PatientId == patientId && o.IssuedAt != null && ObservationStatus.Released.Contains(o.Status))
            .OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Display).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientResults(patient, results);
    }

    // The shortest name a search takes. Every patient listed is a recorded read, and one letter matches hundreds.
    public const int MinimumNameLength = 2;

    // Patients matching every criterion given: a given or family name that starts with the name, in any case; the date
    // of birth; the MRN, exactly. At least one is required, and a name of at least MinimumNameLength characters.
    public Task<PatientList> SearchPatientsAsync(string? name, DateOnly? birthDate, string? mrn, int limit, CancellationToken cancellationToken)
    {
        if (name is null && birthDate is null && mrn is null)
        {
            throw new ArgumentException("A search needs a name, a date of birth or an MRN.");
        }

        if (name is { Length: < MinimumNameLength })
        {
            throw new ArgumentException($"A name to search for has at least {MinimumNameLength} characters.", nameof(name));
        }

        var patients = db.Patients.AsQueryable();
        if (name is not null)
        {
            // A name that starts with the prefix, in any case, is a key from the lower-cased prefix up to, not including,
            // the first string after all those that start with it (PatientConfiguration keeps the keys, in the C
            // collation, which the comparisons take from the column). What was typed is lower-cased in the database,
            // by the lower() that keeps the keys, and the range's end is worked out there from that (NameKeys): made
            // from the query's parameter alone, both are constants to the plan. The range is a condition of its own,
            // plain comparisons, which are leakproof, so under row-level security the keys' indexes can answer it. The
            // prefix check that decides, a second condition, isn't leakproof, so it runs on the rows the range found.
            // When nothing comes after the prefix, for a name of nothing but U+10FFFF, a noncharacter no name holds,
            // the end is null and the range finds nobody. Left so on purpose: an open end (IS NULL OR ...) keeps the
            // index's upper bound only in a custom plan, and in a generic one, as prepared statements get, the scan runs
            // from the prefix to the end of the index (22.7 ms rather than 1.8 at 50,000 patients).
            patients = patients
                .Where(p =>
                    (string.Compare(p.FamilyNameKey, NameKeys.Lower(name)) >= 0
                        && string.Compare(p.FamilyNameKey, NameKeys.After(NameKeys.Lower(name))) < 0)
                    || (string.Compare(p.GivenNameKey, NameKeys.Lower(name)) >= 0
                        && string.Compare(p.GivenNameKey, NameKeys.After(NameKeys.Lower(name))) < 0))
                .Where(p => p.FamilyNameKey.StartsWith(NameKeys.Lower(name)) || p.GivenNameKey.StartsWith(NameKeys.Lower(name)));
        }

        if (birthDate is { } date)
        {
            patients = patients.Where(p => p.BirthDate == date);
        }

        if (mrn is not null)
        {
            patients = patients.Where(p => p.Mrn == mrn);
        }

        return ReadAsync(patients, limit, cancellationToken);
    }

    // The patients on the signed-in clinician's panel (PatientAssignment), the ones the clinic's home shows first, and
    // whether more are on it than the limit.
    public Task<PatientList> PanelAsync(int limit, CancellationToken cancellationToken) =>
        ReadAsync(
            db.Patients.Where(p => db.PatientAssignments.Any(a => a.PatientId == p.Id && a.ClinicianId == user.UserId && a.EndedAt == null)),
            limit, cancellationToken);

    // Called in one repeatable-read transaction so the assignments and chart data are from the same snapshot.
    // Read only actively assigned charts; emergency access never enters the offline cache.
    public async Task<IReadOnlyList<PatientChart>> OfflinePanelAsync(int limit, int visits, CancellationToken cancellationToken)
    {
        var ids = await db.Patients.AsNoTracking()
            .Where(p => db.PatientAssignments.Any(a => a.PatientId == p.Id && a.ClinicianId == user.UserId && a.EndedAt == null))
            .OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var charts = new List<PatientChart>(ids.Count);
        foreach (var id in ids)
        {
            if (await ChartAsync(id, visits, cancellationToken) is { } chart)
            {
                charts.Add(chart);
            }
        }

        return charts;
    }

    public const string ThreadsTable = "message_threads";

    // A patient's message threads (FR-18), latest first, each with how many messages the reader hasn't opened: the
    // patient's own in the portal (those from staff), or the chart's for a clinician (those from the patient). One
    // view of one patient: one recorded read of the patient. Row-level security returns a patient only their own.
    public async Task<PatientThreads?> ThreadsAsync(Guid patientId, bool asPatient, int limit, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var from = asPatient ? MessageSender.Staff : MessageSender.Patient;
        var threads = await db.MessageThreads.AsNoTracking().Where(t => t.PatientId == patientId)
            .OrderByDescending(t => t.LastMessageAt).ThenBy(t => t.Id).Take(limit)
            .Select(t => new ThreadEntry(t.Id, t.Subject, t.LastMessageAt,
                db.Messages.Count(m => m.ThreadId == t.Id && m.SenderKind == from && m.ReadAt == null)))
            .ToListAsync(cancellationToken);
        var allergies = asPatient ? [] : await CurrentAllergies(patientId).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientThreads(patient, threads, allergies);
    }

    // One thread of a patient's, with its messages in order: a recorded read of the thread, whoever reads it. To a
    // patient, staff messages come from "Your care team" and their own from "You"; to staff, each staff message names
    // who wrote it. Null when the thread isn't this patient's.
    public async Task<ThreadDetail?> ThreadAsync(Guid threadId, Guid patientId, bool asPatient, CancellationToken cancellationToken)
    {
        var thread = await db.MessageThreads.AsNoTracking().SingleOrDefaultAsync(t => t.Id == threadId && t.PatientId == patientId, cancellationToken);
        var patient = thread is null ? null : await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (thread is null || patient is null)
        {
            return null;
        }

        var rows = await (
            from message in db.Messages.AsNoTracking()
            join staff in db.StaffUsers on message.SenderId equals staff.Id into senders
            from staff in senders.DefaultIfEmpty()
            where message.ThreadId == threadId
            orderby message.SentAt, message.Id
            select new { message.Id, message.SenderKind, message.SenderId, Staff = staff == null ? null : staff.DisplayName, message.Body, message.SentAt, message.ReadAt })
            .ToListAsync(cancellationToken);

        // A message from the patient's side was sent by the patient's own account or by a proxy's (FR-19). In the
        // portal, the reader's own say "You" and the other's say who: the patient by first name, a proxy as that.
        // Staff see the patient's name, and a proxy's name marked as a proxy.
        var patientSide = rows.Where(m => m.SenderKind == MessageSender.Patient).Select(m => m.SenderId).Distinct().ToList();
        var accounts = patientSide.Count == 0
            ? []
            : await (
                from account in db.PatientUsers.AsNoTracking()
                where patientSide.Contains(account.Id)
                select new { account.Id, account.PatientId }).ToDictionaryAsync(a => a.Id, a => a.PatientId, cancellationToken);
        var proxyPatients = accounts.Values.Where(id => id != patientId).Distinct().ToList();
        var proxyNames = asPatient || proxyPatients.Count == 0
            ? []
            : await db.Patients.AsNoTracking().Where(p => proxyPatients.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => $"{p.GivenName} {p.FamilyName}", cancellationToken);
        string FromPatientSide(Guid sender)
        {
            var byPatient = !accounts.TryGetValue(sender, out var senderPatient) || senderPatient == patientId;
            if (asPatient)
            {
                return sender == user.UserId ? "You" : byPatient ? patient.GivenName : "A proxy";
            }

            return byPatient ? $"{patient.GivenName} {patient.FamilyName}" : $"{proxyNames.GetValueOrDefault(senderPatient, "Someone")} (proxy)";
        }

        var messages = rows.Select(m => new MessageEntry(m.Id, m.SenderKind,
            m.SenderKind == MessageSender.Patient ? FromPatientSide(m.SenderId) : asPatient ? "Your care team" : m.Staff ?? "Staff",
            m.Body, m.SentAt, m.ReadAt)).ToList();
        var other = asPatient ? MessageSender.Staff : MessageSender.Patient;
        var allergies = asPatient ? [] : await CurrentAllergies(patientId).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, ThreadsTable, thread.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new ThreadDetail(thread.Id, thread.Subject, patient, allergies, messages,
            [.. messages.Where(m => m.SenderKind == other && m.ReadAt is null).Select(m => m.Id)]);
    }

    // Results of orders (the expansion plan's step 7).

    // A patient's completed lab orders for the chart: those nobody has acknowledged first, abnormal before the rest,
    // then the latest. Part of the chart's one recorded read.
    async Task<List<ResultedOrder>> ResultedOrdersAsync(Guid patientId, int limit, CancellationToken cancellationToken)
    {
        var rows = await (
            from order in db.Orders.AsNoTracking()
            join item in db.OrderCatalogItems on order.CatalogCode equals item.Code
            where order.PatientId == patientId && order.Status == OrderStatus.Completed
            select new
            {
                order.Id,
                item.Display,
                ResultedAt = db.Observations.Where(o => o.OrderId == order.Id).Max(o => o.IssuedAt),
                Abnormal = db.Observations.Count(o => o.OrderId == order.Id && (o.Value < o.ReferenceLow || o.Value > o.ReferenceHigh)),
                Acknowledged = db.ResultAcknowledgments.Any(a => a.OrderId == order.Id),
            })
            .OrderBy(o => o.Acknowledged).ThenByDescending(o => o.Abnormal > 0).ThenByDescending(o => o.ResultedAt).ThenBy(o => o.Id)
            .Take(limit).ToListAsync(cancellationToken);
        return [.. rows.Select(o => new ResultedOrder(o.Id, o.Display, o.ResultedAt, o.Abnormal, o.Acknowledged))];
    }

    // One order of a patient's with the tests it reports, the results filed so far and who acknowledged them: for the
    // page that enters, shows and acknowledges results. One recorded read of the patient. Null when the order isn't
    // this patient's.
    public async Task<OrderResults?> OrderResultsAsync(Guid patientId, Guid orderId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        var found = patient is null
            ? null
            : await (
                from order in db.Orders.AsNoTracking()
                join item in db.OrderCatalogItems on order.CatalogCode equals item.Code
                join signer in db.StaffUsers on order.SignerId equals signer.Id
                where order.Id == orderId && order.PatientId == patientId
                select new { order.Id, order.CatalogCode, item.Display, order.Type, order.Status, Signer = signer.DisplayName, order.SignedAt })
                .SingleOrDefaultAsync(cancellationToken);
        if (patient is null || found is null)
        {
            return null;
        }

        var expected = await (
            from link in db.OrderResultCodes.AsNoTracking()
            join code in db.ObservationCodes on link.ObservationCode equals code.Code
            where link.CatalogCode == found.CatalogCode
            orderby code.Display, code.Code
            select new { code.Code, code.Display, code.Unit, code.ReferenceLow, code.ReferenceHigh }).ToListAsync(cancellationToken);
        var filed = await db.Observations.AsNoTracking().Where(o => o.OrderId == orderId && o.PatientId == patientId)
            .OrderBy(o => o.Display).ThenBy(o => o.Id).ToListAsync(cancellationToken);
        // The tests the order reports, each with its result when there is one; then any result that isn't one of them
        // (a seeded or interfaced one), so nothing filed is ever left off the page.
        var tests = expected.Select(test =>
        {
            var result = filed.FirstOrDefault(o => o.Code == test.Code);
            return result is null
                ? new OrderTest(test.Code, test.Display, test.Unit, test.ReferenceLow, test.ReferenceHigh, null, null, null)
                : new OrderTest(test.Code, result.Display, result.Unit, result.ReferenceLow, result.ReferenceHigh, result.Value, result.EffectiveAt, result.IssuedAt);
        }).Concat(filed.Where(o => expected.All(test => test.Code != o.Code))
            .Select(o => new OrderTest(o.Code ?? "", o.Display, o.Unit, o.ReferenceLow, o.ReferenceHigh, o.Value, o.EffectiveAt, o.IssuedAt)))
            // Abnormal results first, then the rest as they were.
            .OrderByDescending(test => test.Flag is not null).ToList();
        var acknowledged = await (
            from acknowledgment in db.ResultAcknowledgments.AsNoTracking()
            join staff in db.StaffUsers on acknowledgment.AcknowledgedBy equals staff.Id
            where acknowledgment.OrderId == orderId
            select new { staff.DisplayName, acknowledgment.AcknowledgedAt }).SingleOrDefaultAsync(cancellationToken);
        var allergies = await CurrentAllergies(patientId).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new OrderResults(patient, allergies, found.Id, found.Display, found.Type, found.Status, found.Signer, found.SignedAt, tests,
            acknowledged?.DisplayName, acknowledged?.AcknowledgedAt);
    }

    // Proxy access (FR-19).

    public const string ProxyGrantsTable = "proxy_grants";

    // Who can use the portal for this patient: every grant, the ones in force first, then the latest. For the patient's
    // own access page and the clinician's; a recorded read of the patient. Null when the patient isn't the reader's
    // to see.
    public async Task<(Patient Patient, IReadOnlyList<ProxyGrantEntry> Grants, IReadOnlyList<AllergyIntolerance> Allergies)?> ProxyGrantsAsync(
        Guid patientId, bool asPatient, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        // Those in force first, before the limit, so one that can still be removed is never off the end of the list.
        var rows = await db.ProxyGrants.AsNoTracking().Where(g => g.PatientId == patientId)
            .OrderByDescending(g => g.RevokedAt == null && g.ExpiresAt > now).ThenByDescending(g => g.GrantedAt).ThenBy(g => g.Id).Take(100)
            .ToListAsync(cancellationToken);
        var grants = rows.Select(g => new ProxyGrantEntry(g.Id, g.ProxyEmail.ToLowerInvariant(), g.Scope, g.IncludeSensitive, g.GrantedByKind == "staff",
                g.GrantedAt, g.ExpiresAt, g.RevokedAt, g.RevokedAt is null && g.ExpiresAt > now))
            .OrderByDescending(g => g.InForce).ToList();
        var allergies = asPatient ? [] : await CurrentAllergies(patientId).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return (patient, grants, allergies);
    }

    // The records the signed-in portal account may open as a proxy: the grants addressed to it that are in force,
    // each with whose record it is. The database answers only for the caller's own address, with the patient's name
    // and nothing else of theirs (ehr.proxied_records), so it is not a recorded read: opening one is.
    public async Task<IReadOnlyList<ProxiedRecord>> ProxiedRecordsAsync(CancellationToken cancellationToken)
    {
        var rows = await ProxiedRecords.ListAsync(db, cancellationToken);
        return [.. rows.OrderBy(r => r.FamilyName, StringComparer.Ordinal).ThenBy(r => r.GivenName, StringComparer.Ordinal).ThenBy(r => r.GrantId)
            .Select(r => new ProxiedRecord(r.GrantId, r.PatientId, $"{r.GivenName} {r.FamilyName}", r.Scope, r.IncludeSensitive, r.ExpiresAt))];
    }

    // One of those, by its grant: what a portal session acting under it is looking at, or null when the grant isn't
    // the account's or is no longer in force.
    public async Task<ProxiedRecord?> ProxiedRecordAsync(Guid grantId, CancellationToken cancellationToken) =>
        (await ProxiedRecordsAsync(cancellationToken)).FirstOrDefault(record => record.GrantId == grantId);

    // How many messages from staff a patient hasn't opened yet, across their conversations: whether they have already
    // been told there is something to read. A number, about no particular message: not a recorded read.
    public Task<int> UnreadFromStaffAsync(Guid patientId, CancellationToken cancellationToken) =>
        db.Messages.CountAsync(m => m.PatientId == patientId && m.SenderKind == MessageSender.Staff && m.ReadAt == null, cancellationToken);

    // Appointments, already chosen and limited, with the names the lists show: the clinician's, a staff account anyone
    // signed in may read, and the patient's, which the caller records a read of where it shows it. Sorted here, by
    // start: the joins keep no order.
    async Task<List<AppointmentEntry>> AppointmentEntriesAsync(IQueryable<Appointment> appointments, bool latestFirst, CancellationToken cancellationToken)
    {
        var entries = await (
            from appointment in appointments.AsNoTracking()
            join clinician in db.StaffUsers on appointment.ClinicianId equals clinician.Id
            join patient in db.Patients on appointment.PatientId equals patient.Id
            select new AppointmentEntry(appointment.Id, appointment.PatientId, patient.FamilyName + ", " + patient.GivenName, patient.Mrn,
                appointment.ClinicianId, clinician.DisplayName, appointment.StartsAt, appointment.Minutes, appointment.Reason, appointment.Status,
                appointment.CancelReason, appointment.Version)).ToListAsync(cancellationToken);
        return [.. latestFirst ? entries.OrderByDescending(a => a.StartsAt).ThenBy(a => a.Id) : entries.OrderBy(a => a.StartsAt).ThenBy(a => a.Id)];
    }

    // A clinician's day (FR-17): their appointments that start on that day, UTC, earliest first, whatever their
    // status. Each patient named is a recorded read of that patient, as in a list of patients.
    public async Task<IReadOnlyList<AppointmentEntry>> ScheduleAsync(Guid clinician, DateOnly day, CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var appointments = await AppointmentEntriesAsync(
            db.Appointments.Where(a => a.ClinicianId == clinician && a.StartsAt >= from && a.StartsAt < to), latestFirst: false, cancellationToken);
        if (appointments.Count > 0)
        {
            audit.RecordReads(PatientsTable, appointments.Select(a => a.PatientId).Distinct().Select(patient => (patient, patient)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return appointments;
    }

    // A patient's appointments, for the portal: one view of one patient, so one recorded read of the patient.
    // Row-level security returns a patient only their own.
    public async Task<PatientAppointments?> AppointmentsAsync(Guid patientId, int past, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow.AddHours(-1);
        var mine = db.Appointments.Where(a => a.PatientId == patientId);
        var upcoming = await AppointmentEntriesAsync(
            mine.Where(a => a.Status == AppointmentStatus.Booked && a.StartsAt >= now).OrderBy(a => a.StartsAt).Take(50), latestFirst: false, cancellationToken);
        var earlier = past == 0
            ? []
            : await AppointmentEntriesAsync(
                mine.Where(a => a.Status != AppointmentStatus.Booked || a.StartsAt < now).OrderByDescending(a => a.StartsAt).Take(past),
                latestFirst: true, cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientAppointments(patient, upcoming, earlier);
    }

    // Whose appointment it is, for the page's gate, which comes before anything is read or shown: the ID of the
    // patient and nothing else. Not a recorded read; the read that follows the gate is.
    public Task<Guid?> AppointmentPatientAsync(Guid id, CancellationToken cancellationToken) =>
        db.Appointments.AsNoTracking().Where(a => a.Id == id).Select(a => (Guid?)a.PatientId).SingleOrDefaultAsync(cancellationToken);

    // One appointment, with its patient and their allergies for the banner: one recorded read of the patient.
    public async Task<AppointmentDetail?> AppointmentAsync(Guid id, CancellationToken cancellationToken)
    {
        var appointment = (await AppointmentEntriesAsync(db.Appointments.Where(a => a.Id == id), latestFirst: false, cancellationToken)).SingleOrDefault();
        if (appointment is null)
        {
            return null;
        }

        var patient = await db.Patients.AsNoTracking().SingleAsync(p => p.Id == appointment.PatientId, cancellationToken);
        var allergies = await CurrentAllergies(patient.Id).ToListAsync(cancellationToken);
        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new AppointmentDetail(appointment, patient, allergies);
    }

    // The signed-in staff member's inbox: the items that still need action, oldest first, or the ones done, newest
    // first. Row-level security returns only their own. Each patient named is a recorded read of that patient, as in a
    // list of patients: for a clinician. An admin can't read patients, so gets none of their names and records none.
    public async Task<InboxPage> InboxAsync(bool done, int limit, CancellationToken cancellationToken)
    {
        // Two plain conditions, not one compared with a parameter: each matches its own partial index whatever the plan.
        var mine = db.InboxItems.AsNoTracking().Where(i => i.RecipientId == user.UserId);
        mine = done ? mine.Where(i => i.DoneAt != null) : mine.Where(i => i.DoneAt == null);
        // Still to do: results with a value outside its reference range come first (step 7), then the oldest. Which
        // of the waiting result items are abnormal is worked out first, from those orders' results alone (an index
        // lookup for each order): asked as one sorted query, the database reads every result on file.
        var abnormal = new List<Guid>();
        if (!done)
        {
            var waiting = await mine.Where(i => i.Kind == Ehr.Data.Inbox.InboxItemKind.Result && i.SourceId != null)
                .OrderBy(i => i.CreatedAt).Select(i => i.SourceId!.Value).Take(500).ToListAsync(cancellationToken);
            if (waiting.Count > 0)
            {
                abnormal = await db.Observations.AsNoTracking()
                    .Where(o => o.OrderId != null && waiting.Contains(o.OrderId.Value) && (o.Value < o.ReferenceLow || o.Value > o.ReferenceHigh))
                    .Select(o => o.OrderId!.Value).Distinct().ToListAsync(cancellationToken);
            }
        }

        var ordered = done
            ? mine.OrderByDescending(i => i.DoneAt).ThenBy(i => i.Id)
            : abnormal.Count > 0
                ? mine.OrderByDescending(i => i.SourceId != null && abnormal.Contains(i.SourceId.Value)).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)
                : mine.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id);
        var rows = await (
            from item in ordered.Take(limit + 1)
            join patient in db.Patients on item.PatientId equals patient.Id into patients
            from patient in patients.DefaultIfEmpty()
            select new InboxEntry(item.Id, item.Kind, item.Summary, item.CreatedAt, item.DoneAt, item.PatientId,
                patient == null ? null : patient.FamilyName + ", " + patient.GivenName, patient == null ? null : patient.Mrn, item.SourceTable, item.SourceId))
            .ToListAsync(cancellationToken);
        var items = rows.Select(i => i with { Abnormal = i.SourceId is { } source && abnormal.Contains(source) }).ToList();
        items = [.. done
            ? items.OrderByDescending(i => i.DoneAt).ThenBy(i => i.Id)
            : items.OrderByDescending(i => i.Abnormal).ThenBy(i => i.CreatedAt).ThenBy(i => i.Id)];
        var more = items.Count > limit;
        items = [.. items.Take(limit)];

        var named = items.Where(i => i.PatientName is not null).Select(i => i.PatientId!.Value).Distinct().ToList();
        if (named.Count > 0)
        {
            audit.RecordReads(PatientsTable, named.Select(patient => (patient, patient)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new InboxPage(items, more);
    }

    // How many items in the signed-in staff member's inbox still need action, for the app bar. A number about their
    // own work, which says nothing about any patient: not a recorded read.
    public Task<int> InboxCountAsync(CancellationToken cancellationToken) =>
        db.InboxItems.CountAsync(i => i.RecipientId == user.UserId && i.DoneAt == null, cancellationToken);

    const string DraftsTable = "clinical_drafts";

    public Task<bool> IsAssignedAsync(Guid patient, CancellationToken cancellationToken) =>
        db.PatientAssignments.AnyAsync(a => a.PatientId == patient && a.ClinicianId == user.UserId && a.EndedAt == null, cancellationToken);

    public async Task<ClinicalDraft?> ReadDraftAsync(Guid id, Guid patient, CancellationToken cancellationToken, bool tracking = true)
    {
        var drafts = tracking ? db.ClinicalDrafts.AsQueryable() : db.ClinicalDrafts.AsNoTracking();
        var draft = await drafts.SingleOrDefaultAsync(
            d => d.Id == id && d.PatientId == patient && d.ClinicianId == user.UserId, cancellationToken);
        if (draft is not null)
        {
            audit.Record(AuditActions.Read, draft.PatientId, DraftsTable, draft.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return draft;
    }

    public async Task<IReadOnlyList<ClinicalDraft>> ChangedDraftsAsync(Guid[] patients, ulong since, ulong until, CancellationToken cancellationToken)
    {
        var drafts = await ChangeLog.ClinicalDraftsChanged(db, since, until).AsNoTracking()
            .Where(d => d.ClinicianId == user.UserId && patients.Contains(d.PatientId)).ToListAsync(cancellationToken);
        if (drafts.Count > 0)
        {
            audit.RecordReads(DraftsTable, drafts.Select(d => (d.PatientId, d.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return drafts;
    }

    public async Task<IReadOnlyList<ClinicalDraft>> NoteDraftsAsync(Guid patient, CancellationToken cancel)
    {
        var drafts = await db.ClinicalDrafts.AsNoTracking()
            .Where(d => d.PatientId == patient && d.ClinicianId == user.UserId
                && !db.ClinicalNotes.Any(n => n.DraftId == d.Id))
            .OrderByDescending(d => d.Id).ToListAsync(cancel);
        if (drafts.Count > 0)
        {
            audit.RecordReads(DraftsTable, drafts.Select(d => (d.PatientId, d.Id)));
            await db.SaveChangesAsync(cancel);
        }

        return drafts;
    }

    public async Task<IReadOnlyList<ClinicalNote>> NotesAsync(Guid patient, CancellationToken cancel)
    {
        var notes = await db.ClinicalNotes.AsNoTracking().Where(n => n.PatientId == patient)
            .OrderByDescending(n => n.SignedAt).ThenBy(n => n.Id).ToListAsync(cancel);
        if (notes.Count > 0)
        {
            audit.RecordReads("clinical_notes", notes.Select(n => (n.PatientId, n.Id)));
            await db.SaveChangesAsync(cancel);
        }

        return notes;
    }

    public Task<ClinicalNote?> SignedDraftAsync(Guid draft, Guid patient, CancellationToken cancel) =>
        ReadNoteAsync(db.ClinicalNotes.Where(n => n.DraftId == draft && n.PatientId == patient), cancel);

    public Task<ClinicalNote?> NoteAsync(Guid id, Guid patient, CancellationToken cancel) =>
        ReadNoteAsync(db.ClinicalNotes.Where(n => n.Id == id && n.PatientId == patient), cancel);

    async Task<ClinicalNote?> ReadNoteAsync(IQueryable<ClinicalNote> query, CancellationToken cancel)
    {
        var note = await query.AsNoTracking().SingleOrDefaultAsync(cancel);
        if (note is not null)
        {
            audit.Record(AuditActions.Read, note.PatientId, "clinical_notes", note.Id);
            await db.SaveChangesAsync(cancel);
        }

        return note;
    }

    public async Task<ClinicalDraft?> LockDraftAsync(Guid id, Guid patient, CancellationToken cancel) =>
        await ClinicalDraftLock.AcquireAsync(db, id, patient, cancel) ? await ReadDraftAsync(id, patient, cancel) : null;

    public async Task<AiNoteReview?> AiReviewAsync(Guid id, CancellationToken cancel, bool tracking = false)
    {
        var query = tracking ? db.AiNoteReviews.AsQueryable() : db.AiNoteReviews.AsNoTracking();
        var review = await query.SingleOrDefaultAsync(r => r.Id == id && r.ClinicianId == user.UserId, cancel);
        if (review is not null)
        {
            audit.Record(AuditActions.Read, review.PatientId, "ai_note_reviews", review.Id);
            await db.SaveChangesAsync(cancel);
        }

        return review;
    }

    public async Task<IReadOnlyList<AiNoteReview>> AiReviewsAsync(Guid draft, CancellationToken cancel)
    {
        var reviews = await db.AiNoteReviews.AsNoTracking().Where(r => r.DraftId == draft && r.ClinicianId == user.UserId)
            .OrderBy(r => r.CreatedAt).ToListAsync(cancel);
        if (reviews.Count > 0)
        {
            audit.RecordReads("ai_note_reviews", reviews.Select(r => (r.PatientId, r.Id)));
            await db.SaveChangesAsync(cancel);
        }

        return reviews;
    }

    public async Task<IReadOnlyList<AiFieldProposal>> AiProposalsAsync(Guid review, CancellationToken cancel, bool tracking = false)
    {
        var query = tracking ? db.AiFieldProposals.AsQueryable() : db.AiFieldProposals.AsNoTracking();
        var proposals = await query.Where(p => p.ReviewId == review).OrderBy(p => p.Field).ToListAsync(cancel);
        if (proposals.Count > 0)
        {
            audit.RecordReads("ai_field_proposals", proposals.Select(p => (p.PatientId, p.Id)));
            await db.SaveChangesAsync(cancel);
        }

        return proposals;
    }

    // A list of patients, in name order, each one a recorded read, and whether more match than the limit.
    async Task<PatientList> ReadAsync(IQueryable<Patient> query, int limit, CancellationToken cancellationToken)
    {
        var ordered = query.AsNoTracking().OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id);
        var patients = await ordered.Take(limit).ToListAsync(cancellationToken);
        // Only a yes or no comes back, nobody's data, so there is nothing more to record.
        var more = patients.Count == limit && await ordered.Skip(limit).AnyAsync(cancellationToken);
        if (patients.Count > 0)
        {
            audit.RecordReads(PatientsTable, patients.Select(p => (p.Id, p.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PatientList(patients, more);
    }
}

// Patients listed by PhiReader, each already recorded as a read, and whether more matched than were listed.
public sealed record PatientList(IReadOnlyList<Patient> Patients, bool More);
