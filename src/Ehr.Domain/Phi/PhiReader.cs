using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Lab;
using Ehr.Data.Messaging;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Ehr.Data.Proxies;
using Ehr.Data.Scheduling;
using Ehr.Domain.Audit;
using Ehr.Domain.Messaging;
using Ehr.Domain.Orders;
using Ehr.Domain.Proxies;
using Ehr.Domain.Refills;
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
        Guid id, int visits, CancellationToken cancellationToken, Guid? discontinued = null, bool sensitive = false, bool notes = true)
    {
        // With the patient, in the same statement: which of the chart's occasional sections have anything in them.
        // Most charts have no appointment booked, no conversation, no resulted order waiting and no notes, and each
        // empty section then costs an index probe here instead of a query and a round trip of its own. The page is
        // about sixteen round trips otherwise, which is most of its time.
        var soon = DateTimeOffset.UtcNow.AddHours(-1);
        var found = await db.Patients.AsNoTracking().Where(p => p.Id == id)
            .Select(p => new
            {
                Patient = p,
                Upcoming = db.Appointments.Any(a => a.PatientId == id && a.Status == AppointmentStatus.Booked && a.StartsAt >= soon),
                Threads = db.MessageThreads.Any(t => t.PatientId == id),
                Resulted = db.Orders.Any(o => o.PatientId == id && o.Status == OrderStatus.Completed),
                Notes = db.ClinicalNotes.Any(n => n.PatientId == id),
                // Signed notes marked sensitive (step 10): counted here, in the patient's statement, and left out of
                // the chart's list unless asked for, like sensitive diagnoses and medications.
                SensitiveNotes = db.ClinicalNotes.Count(n => n.PatientId == id && n.Sensitive),
                Drafts = db.ClinicalDrafts.Any(d => d.PatientId == id),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var patient = found.Patient;
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
        // The patient's newest vital signs (step 9), in one plain query: the chart's short flowsheet takes the newest
        // value of each sign from it, and each visit shown takes the blood pressure and heart rate taken at it. 120
        // values is thirteen full sets, or forty visits' three signs. One statement, because off-site each statement
        // costs the chart far more than on-site (measured: about 3 ms at the median for this one, however it was
        // written; two cost more).
        var newestVitals = (await db.Observations.AsNoTracking()
                .Where(o => o.PatientId == id && o.Category == ObservationCategory.VitalSigns && o.Value != null)
                .OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Code).Take(120).ToListAsync(cancellationToken))
                .Where(o => ObservationStatus.Released.Contains(o.Status))
                .OrderByDescending(o => o.EffectiveAt).ThenByDescending(o => o.IssuedAt).ToList();
        decimal? At(Guid visit, string code) => newestVitals.FirstOrDefault(o => o.EncounterId == visit && o.Code == code)?.Value;
        // A blood pressure is one reading (step 9c): a visit's, and the newest, are each one reading's two numbers.
        var pressures = BloodPressure.Readings(newestVitals);
        var visitVitals = encounters.ToDictionary(e => e.Id, e => VisitVitals.Of(pressures.FirstOrDefault(p => p.Systolic.EncounterId == e.Id), At(e.Id, VitalSign.HeartRate)));
        var latestVitals = newestVitals.Where(o => !VitalSign.BloodPressure.Contains(o.Code)).Select(LatestVital.Of);
        var orders = await ActiveOrders(id, catalogCode: null).ToListAsync(cancellationToken);
        // Only when the discontinue page sent the clinician here: whether that order is this patient's, and discontinued.
        var confirmed = discontinued is not null && await db.Orders.AsNoTracking()
            .AnyAsync(order => order.Id == discontinued && order.PatientId == id && order.Status == OrderStatus.Revoked, cancellationToken);

        var hidden = 0;
        if (!sensitive)
        {
            hidden = medications.RemoveAll(m => m.Sensitive) + conditions.RemoveAll(c => c.Sensitive);
        }

        // The chart's list of signed notes (step 10c): when and whether sensitive, not their text, read here so their
        // reads are recorded with the chart's in one insert. It was a read and an insert of its own, which put a
        // chart with notes over the gate on a slow CI runner. Sensitive ones only when asked for (step 10). Not for
        // the offline copy, which reads the notes it keeps, text and all, itself (`notes: false`). Whether each has a
        // standing visit summary (step 13a) is asked in the same statement, by the index on the standing summaries.
        IReadOnlyList<SignedNoteEntry> signed = !notes || !found.Notes
            ? []
            : await db.ClinicalNotes.AsNoTracking().Where(n => n.PatientId == id && (sensitive || !n.Sensitive))
                .OrderByDescending(n => n.SignedAt).ThenBy(n => n.Id)
                .Select(n => new SignedNoteEntry(n.Id, n.SignedAt, n.Sensitive)
                {
                    SummaryReleased = db.VisitSummaries.Any(s => s.NoteId == n.Id && s.PatientId == id && s.WithdrawnAt == null),
                })
                .ToListAsync(cancellationToken);
        // One event for one view: a read, or a read that showed sensitive items. Then each note listed, as before.
        audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        if (signed.Count > 0)
        {
            audit.RecordReads("clinical_notes", signed.Select(n => (id, n.Id)));
        }

        await db.SaveChangesAsync(cancellationToken);
        var upcoming = !found.Upcoming
            ? []
            : await AppointmentEntriesAsync(
                db.Appointments.Where(a => a.PatientId == id && a.Status == AppointmentStatus.Booked && a.StartsAt >= soon).OrderBy(a => a.StartsAt).Take(5),
                latestFirst: false, cancellationToken);
        var threads = !found.Threads
            ? []
            : await db.MessageThreads.AsNoTracking().Where(t => t.PatientId == id)
                .OrderByDescending(t => t.LastMessageAt).ThenBy(t => t.Id).Take(5)
                .Select(t => new ThreadEntry(t.Id, t.Subject, t.LastMessageAt,
                    db.Messages.Count(m => m.ThreadId == t.Id && m.SenderKind == MessageSender.Patient && m.ReadAt == null)))
                .ToListAsync(cancellationToken);
        var resulted = found.Resulted ? await ResultedOrdersAsync(id, 8, cancellationToken) : [];
        return new PatientChart(patient, allergies, medications, conditions, encounters, orders)
        {
            HasNotes = found.Notes,
            Notes = signed,
            HasDrafts = found.Drafts,
            VisitVitals = visitVitals,
            // The newest of each sign, in the flowsheet's order.
            // With the reading before it, for the up or down mark, from the same rows: no other statement (step 9b).
            LatestVitals = [.. latestVitals.GroupBy(v => v.Code).Select(group => group.First() with { Previous = group.Skip(1).FirstOrDefault()?.Value })
                .Concat(LatestVital.Pressure(pressures)).OrderBy(v => Array.IndexOf(VitalSign.All, v.Code))],
            Resulted = resulted,
            Threads = threads,
            Upcoming = upcoming,
            ConfirmsDiscontinued = confirmed,
            SensitiveHidden = hidden,
            SensitiveNotesHidden = sensitive ? 0 : found.SensitiveNotes,
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
    // Step 13d: `refill` is a refill request the order approves; it comes back when it is this patient's, with whether the
    // chosen item renews its medication, and showing it is a view of sensitive items when the request is sensitive.
    public async Task<OrderForm?> OrderFormAsync(Guid id, string? catalogCode, Guid? replaces, CancellationToken cancellationToken, Guid? refill = null)
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

        var warnings = catalogCode is null ? [] : await OrderSafety.WarningsAsync(db, id, catalogCode, replaces, cancellationToken);
        var request = refill is { } asked ? await ReadRefillAsync(patient, allergies, asked, cancellationToken) : null;
        var renews = request is not null && catalogCode is not null && catalogCode == request.Medication.Code;

        audit.Record(request?.Sensitive == true ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new OrderForm(patient, allergies, duplicates, replaced) { Warnings = warnings, Refill = request, RenewsRefill = renews };
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
            // Laboratory results: vital signs are observations too since step 9, and are read apart, below, so that
            // however many there are they never crowd a result out.
            .Where(o => o.PatientId == patientId && o.Category == ObservationCategory.Laboratory && o.IssuedAt != null && ObservationStatus.Released.Contains(o.Status))
            .OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Display).ThenBy(o => o.Id)
            .ToListAsync(cancellationToken);
        // The vital signs for the page's trends: the newest 600 values, which is more than the charts draw.
        var vitals = await db.Observations.AsNoTracking()
            .Where(o => o.PatientId == patientId && o.Category == ObservationCategory.VitalSigns && o.Value != null && o.IssuedAt != null
                && ObservationStatus.Released.Contains(o.Status))
            .OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Code).Take(600).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientResults(patient, results) { Vitals = vitals };
    }

    // The shortest name a search takes. Every patient listed is a recorded read, and one letter matches hundreds.
    public const int MinimumNameLength = 2;

    // The longest an MRN is (PatientConfiguration). A value longer than that, or with a control character in it, such
    // as a NUL, which PostgreSQL refuses in text, can't be an MRN: the patient search page and the FHIR API refuse it
    // before asking the database.
    public const int MaximumMrnLength = 20;

    public static bool CouldBeMrn(string mrn) => mrn.Length <= MaximumMrnLength && !mrn.Any(char.IsControl);

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
            if (await ChartAsync(id, visits, cancellationToken, notes: false) is { } chart)
            {
                // The signed notes that may be kept offline, with their addenda (step 10): only when there are any.
                charts.Add(chart.HasNotes ? chart with { OfflineNotes = await OfflineNotesAsync(id, limit: 10, cancellationToken) } : chart);
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
                select new { account.Id, account.PatientId, account.DisplayName }).ToDictionaryAsync(a => a.Id, a => (a.PatientId, a.DisplayName), cancellationToken);
        // A proxy who is a patient here is named by their own record; a proxy-only account by the name it gave.
        var proxyPatients = accounts.Values.Where(a => a.PatientId is not null && a.PatientId != patientId).Select(a => a.PatientId!.Value).Distinct().ToList();
        var proxyNames = asPatient || proxyPatients.Count == 0
            ? []
            : await db.Patients.AsNoTracking().Where(p => proxyPatients.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => $"{p.GivenName} {p.FamilyName}", cancellationToken);
        string FromPatientSide(Guid sender)
        {
            var byPatient = !accounts.TryGetValue(sender, out var account) || account.PatientId == patientId;
            if (asPatient)
            {
                return sender == user.UserId ? "You" : byPatient ? patient.GivenName : "A proxy";
            }

            if (byPatient)
            {
                return $"{patient.GivenName} {patient.FamilyName}";
            }

            var name = account.PatientId is { } own ? proxyNames.GetValueOrDefault(own) : account.DisplayName;
            return $"{(string.IsNullOrWhiteSpace(name) ? "Someone" : name)} (proxy)";
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
        // Every value the patient has of the tests this order reports, for their trends: only when it has results.
        var codes = filed.Where(o => o.Code is not null).Select(o => o.Code!).Distinct().ToList();
        var history = codes.Count == 0
            ? []
            : await db.Observations.AsNoTracking()
                .Where(o => o.PatientId == patientId && o.Category == ObservationCategory.Laboratory && codes.Contains(o.Code!) && o.Value != null
                    && ObservationStatus.Released.Contains(o.Status))
                .OrderByDescending(o => o.EffectiveAt).Take(500).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new OrderResults(patient, allergies, found.Id, found.Display, found.Type, found.Status, found.Signer, found.SignedAt, tests,
            acknowledged?.DisplayName, acknowledged?.AcknowledgedAt)
        {
            Trends = Trend.From(history),
        };
    }

    // A patient's vital signs (step 9): the latest first, up to 600 values, for the flowsheet and their trends. One
    // recorded read of the patient. Null when the patient isn't the reader's to see.
    // Entries marked "entered in error" (step 9b) are among them, for the page to leave out or show struck through.
    public async Task<PatientVitals?> VitalsAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var signs = await db.Observations.AsNoTracking()
            .Where(o => o.PatientId == patientId && o.Category == ObservationCategory.VitalSigns && o.Value != null)
            .OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Code).Take(600).ToListAsync(cancellationToken);
        var allergies = await CurrentAllergies(patientId).ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientVitals(patient, allergies, signs);
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

    // A clinician's day (FR-17): their appointments that start between two moments (a day of the clinic's calendar, step 10d), earliest first, whatever their
    // status. Each patient named is a recorded read of that patient, as in a list of patients.
    public async Task<IReadOnlyList<AppointmentEntry>> ScheduleAsync(Guid clinician, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
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

    // What a lab is handed (the expansion plan's step 12): the lab orders signed since `since` that still wait for
    // results, as the database writes them. No account reads here, so this records nothing itself: the database
    // records each order it hands over, in the statement that reads it, with the lab as the source, so nothing leaves
    // it without its record (db/policies/76-lab.sql). The hash of the lab's key decides whether there is anything at all.
    public Task<LabHandOver> LabOrdersAsync(string keyHash, ulong since, CancellationToken cancellationToken) =>
        LabStore.OrdersAsync(db, keyHash, since, cancellationToken);

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

    // Whether a draft is signed, and its note: the check the draft pages and the sync make before anything else. It
    // shows nobody the note, so for a sensitive note it records the read of the row and not a view of sensitive items.
    public Task<ClinicalNote?> SignedDraftAsync(Guid draft, Guid patient, CancellationToken cancel) =>
        ReadNoteAsync(db.ClinicalNotes.Where(n => n.DraftId == draft && n.PatientId == patient), shown: false, cancel);

    // A signed note for a page that shows it. `shown: false` is for a request that only acts on it (signing an
    // addendum) and sends the reader on.
    public Task<ClinicalNote?> NoteAsync(Guid id, Guid patient, CancellationToken cancel, bool shown = true) =>
        ReadNoteAsync(db.ClinicalNotes.Where(n => n.Id == id && n.PatientId == patient), shown, cancel);

    // Of these drafts, the ones signed as a sensitive note (step 10): the desktop sync withholds their text.
    public async Task<HashSet<Guid>> SensitiveSignedDraftsAsync(IReadOnlyCollection<Guid> drafts, CancellationToken cancel) =>
        drafts.Count == 0 ? [] : [.. await db.ClinicalNotes.AsNoTracking().Where(n => drafts.Contains(n.DraftId) && n.Sensitive).Select(n => n.DraftId).ToListAsync(cancel)];

    async Task<ClinicalNote?> ReadNoteAsync(IQueryable<ClinicalNote> query, bool shown, CancellationToken cancel)
    {
        var note = await query.AsNoTracking().SingleOrDefaultAsync(cancel);
        if (note is not null)
        {
            audit.Record(AuditActions.Read, note.PatientId, "clinical_notes", note.Id);
            if (note.Sensitive && shown)
            {
                // Opening a sensitive note is a view of the patient's sensitive items, recorded as the chart's is.
                audit.Record(AuditActions.ReadSensitive, note.PatientId, PatientsTable, note.PatientId);
            }

            await db.SaveChangesAsync(cancel);
        }

        return note;
    }

    // A signed note with its addenda (step 10) and its visit summaries (step 13a), for the page that shows them: one
    // recorded read of the note, which covers them. Sensitive addenda and summaries of a note that isn't sensitive are
    // left out unless asked for, and showing any of them is one recorded view of the patient's sensitive items. Null
    // when the note isn't this patient's or isn't the reader's to see.
    public async Task<(ClinicalNote Note, NoteAddenda Addenda, NoteSummaries Summaries)?> NoteWithAddendaAsync(Guid id, Guid patient, bool sensitive, CancellationToken cancel)
    {
        if (await NoteAsync(id, patient, cancel) is not { } note)
        {
            return null;
        }

        var addenda = await AddendaAsync(note, sensitive, cancel, record: false);
        var summaries = await NoteSummariesAsync(note, cancel);
        var show = sensitive || note.Sensitive;
        IReadOnlyList<VisitSummaryEntry> shown = show ? summaries : [.. summaries.Where(s => !s.Sensitive)];
        if (show && !note.Sensitive && (addenda.All.Any(a => a.Sensitive) || summaries.Any(s => s.Sensitive)))
        {
            audit.Record(AuditActions.ReadSensitive, note.PatientId, PatientsTable, note.PatientId);
            await db.SaveChangesAsync(cancel);
        }

        return (note, addenda, new NoteSummaries(shown, summaries.Count - shown.Count, show) { HasStanding = summaries.Any(s => s.WithdrawnAt is null) });
    }

    // One addendum of a note, for the page that marks it sensitive or unmarks it (step 10b): who wrote it and when,
    // whether it is sensitive and who made it so. Its text is read with it and not shown there. The page has just
    // read the note, which is the recorded read. Null when it isn't an addendum of that note.
    public async Task<AddendumEntry?> AddendumAsync(ClinicalNote note, Guid addendum, CancellationToken cancel)
    {
        var entry = (await AddendaAsync(note, sensitive: false, cancel, only: addendum)).All.SingleOrDefault();
        if (entry is { Sensitive: true })
        {
            // The page says who wrote a sensitive addendum and when, to someone who may not have asked to see it:
            // recorded as a view of the patient's sensitive items.
            audit.Record(AuditActions.ReadSensitive, note.PatientId, PatientsTable, note.PatientId);
            await db.SaveChangesAsync(cancel);
        }

        return entry;
    }

    // The addenda to a note just read, in the order they were signed, each with its author's name. Sensitive addenda
    // to a note that isn't sensitive are left out unless asked for, and showing them is recorded as a view of the
    // patient's sensitive items; a sensitive note's addenda are all sensitive, and its read has already recorded that.
    async Task<NoteAddenda> AddendaAsync(ClinicalNote note, bool sensitive, CancellationToken cancel, Guid? only = null, bool record = true)
    {
        var all = await db.NoteAddenda.AsNoTracking().Where(a => a.NoteId == note.Id && a.PatientId == note.PatientId && (only == null || a.Id == only))
            .OrderBy(a => a.SignedAt).ThenBy(a => a.Id)
            .Select(a => new AddendumEntry(a.Id, a.Text, a.SignedAt, a.Sensitive, a.AuthorId,
                db.StaffUsers.Where(u => u.Id == a.AuthorId).Select(u => u.DisplayName).FirstOrDefault() ?? "")
            {
                ByNoteAuthor = a.AuthorId == note.SignerId,
                SensitiveInherited = a.SensitiveInherited,
                SensitiveMarkedBy = a.SensitiveMarkedBy,
                SensitiveReason = a.SensitiveReason,
                SensitiveChangedAt = a.SensitiveChangedAt,
            })
            .ToListAsync(cancel);
        var show = sensitive || note.Sensitive;
        var shown = show ? all : all.Where(a => !a.Sensitive).ToList();
        if (record && show && !note.Sensitive && all.Any(a => a.Sensitive))
        {
            audit.Record(AuditActions.ReadSensitive, note.PatientId, PatientsTable, note.PatientId);
            await db.SaveChangesAsync(cancel);
        }

        return new NoteAddenda(shown, all.Count - shown.Count, show) { All = all };
    }

    // For the desktop app's offline copy (step 10): a patient's latest signed notes that aren't sensitive, each with
    // its addenda that aren't, newest note first. Sensitive ones are never kept offline. Each note read is recorded.
    async Task<IReadOnlyList<OfflineNote>> OfflineNotesAsync(Guid patient, int limit, CancellationToken cancel)
    {
        var notes = await db.ClinicalNotes.AsNoTracking().Where(n => n.PatientId == patient && !n.Sensitive)
            .OrderByDescending(n => n.SignedAt).ThenBy(n => n.Id).Take(limit)
            .Select(n => new { Note = n, Signer = db.StaffUsers.Where(u => u.Id == n.SignerId).Select(u => u.DisplayName).FirstOrDefault() })
            .ToListAsync(cancel);
        if (notes.Count == 0)
        {
            return [];
        }

        var ids = notes.Select(n => n.Note.Id).ToList();
        var addenda = await db.NoteAddenda.AsNoTracking().Where(a => ids.Contains(a.NoteId) && a.PatientId == patient && !a.Sensitive)
            .OrderBy(a => a.SignedAt).ThenBy(a => a.Id)
            .Select(a => new
            {
                a.NoteId,
                Entry = new AddendumEntry(a.Id, a.Text, a.SignedAt, a.Sensitive, a.AuthorId,
                db.StaffUsers.Where(u => u.Id == a.AuthorId).Select(u => u.DisplayName).FirstOrDefault() ?? "")
            })
            .ToListAsync(cancel);
        audit.RecordReads("clinical_notes", notes.Select(n => (n.Note.PatientId, n.Note.Id)));
        await db.SaveChangesAsync(cancel);
        // How many sensitive addenda each note has that aren't in the copy, so the desktop can say so.
        var withheld = await db.NoteAddenda.AsNoTracking().Where(a => ids.Contains(a.NoteId) && a.PatientId == patient && a.Sensitive)
            .GroupBy(a => a.NoteId).Select(group => new { NoteId = group.Key, Count = group.Count() }).ToDictionaryAsync(g => g.NoteId, g => g.Count, cancel);
        return [.. notes.Select(n => new OfflineNote(n.Note, n.Signer ?? "",
            [.. addenda.Where(a => a.NoteId == n.Note.Id).Select(a => a.Entry with { ByNoteAuthor = a.Entry.AuthorId == n.Note.SignerId })])
        {
            SensitiveAddendaNotKept = withheld.GetValueOrDefault(n.Note.Id),
        })];
    }

    // The visit summaries written from a note just read (step 13a): the standing one first, then those withdrawn,
    // latest first, each with who released it and who withdrew it. For a page that says whether one stands and who
    // released it, and shows none of their text: the page has read the note, which is the recorded read. The note's
    // own page has them through NoteWithAddendaAsync, which leaves sensitive ones out unless asked for.
    public async Task<IReadOnlyList<VisitSummaryEntry>> NoteSummariesAsync(ClinicalNote note, CancellationToken cancel) =>
        await SummaryEntries(db.VisitSummaries.Where(s => s.NoteId == note.Id && s.PatientId == note.PatientId)
            .OrderBy(s => s.WithdrawnAt != null).ThenByDescending(s => s.ReleasedAt).ThenBy(s => s.Id)).ToListAsync(cancel);

    // One summary of a note just read, for the page that withdraws it. That page shows the summary and not the note, so
    // a sensitive one is recorded as a view of the patient's sensitive items, as an addendum's page records one. Null
    // when it isn't a summary of that note.
    public async Task<VisitSummaryEntry?> NoteSummaryAsync(ClinicalNote note, Guid summary, CancellationToken cancel)
    {
        var entry = await SummaryEntries(db.VisitSummaries.Where(s => s.Id == summary && s.NoteId == note.Id && s.PatientId == note.PatientId))
            .SingleOrDefaultAsync(cancel);
        if (entry is { Sensitive: true })
        {
            audit.Record(AuditActions.ReadSensitive, note.PatientId, PatientsTable, note.PatientId);
            await db.SaveChangesAsync(cancel);
        }

        return entry;
    }

    // Whether a note just read, or any of its addenda, is sensitive: then a summary of it is sensitive too, inherited
    // (step 13a). A yes or no about the note, not a recorded read.
    public async Task<bool> NoteSourcesSensitiveAsync(ClinicalNote note, CancellationToken cancel) =>
        note.Sensitive || await db.NoteAddenda.AnyAsync(a => a.NoteId == note.Id && a.PatientId == note.PatientId && a.Sensitive, cancel);

    public const string SummariesTable = "visit_summaries";

    // A patient's visit summaries for the portal (step 13a): the standing ones, newest note first, up to the limit, and
    // the withdrawn ones as their dates alone. The portal reads no summary's row: the database gives it, for the record
    // the session is looking at, what of each a patient may see and only what the patient's rule allows: their own
    // sensitive ones, a proxy's only when the patient allowed sensitive items (ehr.standing_visit_summaries and
    // ehr.withdrawn_visit_summaries). Sensitive ones are left out unless asked for, as everywhere: asking is recorded as a
    // view of the patient's sensitive items, in place of the plain read. One recorded read of the patient, and a read of
    // each summary shown, by which the database knows a withdrawn one had been viewed (Tahseen's decision of 21 November
    // 2026). Null when the session may read no such patient.
    public async Task<PatientVisitSummaries?> VisitSummariesAsync(Guid patientId, bool sensitive, int limit, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var standing = await VisitSummaryChecks.StandingAsync(db, limit, cancellationToken);
        var withdrawn = await VisitSummaryChecks.WithdrawnAsync(db, cancellationToken);
        var hidden = sensitive ? 0 : standing.Count(s => s.Sensitive) + withdrawn.Count(w => w.Sensitive);
        List<PortalVisitSummary> shown = sensitive ? standing : [.. standing.Where(s => !s.Sensitive)];
        // Up to the page's limit and one: in one statement, or one write to the cloud app's queue.
        await audit.RecordAllAsync(
            [(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id),
                .. shown.Select(s => (AuditActions.Read, patient.Id, SummariesTable, s.Id))],
            cancellationToken);
        return new PatientVisitSummaries(patient, shown, sensitive ? withdrawn : [.. withdrawn.Where(w => !w.Sensitive)], hidden, sensitive);
    }

    // How many visit summaries were released for a patient in the last day, the one just released included: whether the
    // patient has already been told today that there is something new. A number, about no summary in particular: not a
    // recorded read.
    public Task<int> SummariesReleasedTodayAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        return db.VisitSummaries.CountAsync(s => s.PatientId == patientId && s.ReleasedAt > since, cancellationToken);
    }

    public const string AuditTable = "audit_events";

    // Who viewed the record a portal session is looking at (the expansion plan's step 13c): a page of its views in the
    // audit log, newest first, older or newer than a moment, from ehr.record_views (db/policies/42-record-views.sql),
    // which answers only for the session's own record and only with the lines its viewer may see. Looking is a read of
    // the patient's part of the log, recorded once, after reading, so it is listed from the next look on. Null when the
    // session may read no such patient.
    public async Task<IReadOnlyList<RecordViewRow>?> RecordViewsAsync(Guid patientId, DateTimeOffset? olderThan, DateTimeOffset? newerThan, CancellationToken cancellationToken)
    {
        if (!await db.Patients.AsNoTracking().AnyAsync(p => p.Id == patientId, cancellationToken))
        {
            return null;
        }

        var views = await RecordViews.ReadAsync(db, olderThan, newerThan, cancellationToken);
        audit.Record(AuditActions.Read, patientId, AuditTable);
        await db.SaveChangesAsync(cancellationToken);
        return views;
    }

    // Summaries as the pages show them, with the names of who released and withdrew each. Staff accounts anyone signed
    // in may read.
    IQueryable<VisitSummaryEntry> SummaryEntries(IQueryable<VisitSummary> summaries) =>
        summaries.AsNoTracking().Select(s => new VisitSummaryEntry(s.Id, s.TalkedAbout, s.Found, s.NextSteps, s.NoteSignedAt,
            db.StaffUsers.Where(u => u.Id == s.ReleasedBy).Select(u => u.DisplayName).FirstOrDefault() ?? "", s.ReleasedAt, s.Sensitive)
        {
            ReleasedById = s.ReleasedBy,
            SensitiveInherited = s.SensitiveInherited,
            SensitiveMarkedBy = s.SensitiveMarkedBy,
            SensitiveReason = s.SensitiveReason,
            SensitiveChangedAt = s.SensitiveChangedAt,
            WithdrawnAt = s.WithdrawnAt,
            WithdrawnBy = db.StaffUsers.Where(u => u.Id == s.WithdrawnBy).Select(u => u.DisplayName).FirstOrDefault(),
            WithdrawnReason = s.WithdrawnReason,
        });

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

    // The FHIR API (the expansion plan's step 11). Each read of entries below is one view of one patient, so one recorded
    // read of the patient, as the chart's is: `read`, or `read_sensitive` when sensitive items were asked for, which is
    // what the chart records too. Entries of every status, but for those entered in error. `only` reads one entry by its
    // ID, and records nothing when it isn't there. Each public read records; the Read...Async beneath it reads the same
    // and records nothing, for a read that records one event for several kinds of entry.

    // Whose entry it is, for the API's gate, which comes before anything is read: the patient's ID and nothing else, or
    // null when the reader has no such entry. A sensitive diagnosis or medication is found only when sensitive items
    // were asked for, and an entry in error never is: so none of those is revealed. A blood pressure is found by its
    // reading's ID (step 9c), never by the ID of one of its two values, which are not entries of their own. Not a recorded
    // read; the read that follows the gate is.
    public async Task<Guid?> EntryPatientAsync(string table, Guid id, bool sensitive, CancellationToken cancellationToken) =>
        table switch
        {
            "allergy_intolerances" => await db.AllergyIntolerances.AsNoTracking().Where(a => a.Id == id)
                .Select(a => (Guid?)a.PatientId).SingleOrDefaultAsync(cancellationToken),
            "medication_statements" => await db.MedicationStatements.AsNoTracking()
                .Where(m => m.Id == id && (sensitive || !m.Sensitive) && m.Status != MedicationStatementStatus.EnteredInError)
                .Select(m => (Guid?)m.PatientId).SingleOrDefaultAsync(cancellationToken),
            "conditions" => await db.Conditions.AsNoTracking().Where(c => c.Id == id && (sensitive || !c.Sensitive))
                .Select(c => (Guid?)c.PatientId).SingleOrDefaultAsync(cancellationToken),
            "observations" => await ObservationById(id).AsNoTracking().Where(o => o.Status != ObservationStatus.EnteredInError)
                .Select(o => (Guid?)o.PatientId).FirstOrDefaultAsync(cancellationToken),
            "appointments" => await AppointmentPatientAsync(id, cancellationToken),
            _ => throw new ArgumentException($"No entries of '{table}' are read by ID.", nameof(table)),
        };

    // The ID of the patient with this MRN, for the API's gate, before anything about them is read. Not a recorded read.
    public Task<Guid?> PatientIdByMrnAsync(string mrn, CancellationToken cancellationToken) =>
        db.Patients.AsNoTracking().Where(p => p.Mrn == mrn).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(cancellationToken);

    // A patient's allergies, every status, the latest recorded first. "No known allergies" is one of them.
    public async Task<PatientEntries<AllergyIntolerance>> AllergiesAsync(Guid patientId, Guid? only, int limit, CancellationToken cancellationToken) =>
        await RecordAsync(patientId, only, await ReadAllergiesAsync(patientId, only, limit, cancellationToken), sensitive: false, cancellationToken);

    async Task<PatientEntries<AllergyIntolerance>> ReadAllergiesAsync(Guid patientId, Guid? only, int limit, CancellationToken cancellationToken)
    {
        var all = await db.AllergyIntolerances.AsNoTracking().Where(a => a.PatientId == patientId && (only == null || a.Id == only))
            .OrderByDescending(a => a.RecordedAt).ThenBy(a => a.Id).ToListAsync(cancellationToken);
        return Entries(all, sensitiveLeftOut: 0, limit);
    }

    // A patient's medications, every status but entered in error, the latest started first, undated last. Sensitive ones
    // only when asked for; like the chart, they are read either way and left out here, so the API can say how many it
    // left out.
    public async Task<PatientEntries<MedicationStatement>> MedicationsAsync(Guid patientId, Guid? only, bool sensitive, int limit, CancellationToken cancellationToken) =>
        await RecordAsync(patientId, only, await ReadMedicationsAsync(patientId, only, sensitive, limit, cancellationToken), sensitive, cancellationToken);

    async Task<PatientEntries<MedicationStatement>> ReadMedicationsAsync(Guid patientId, Guid? only, bool sensitive, int limit, CancellationToken cancellationToken)
    {
        var all = await db.MedicationStatements.AsNoTracking()
            .Where(m => m.PatientId == patientId && (only == null || m.Id == only) && m.Status != MedicationStatementStatus.EnteredInError)
            .OrderBy(m => m.EffectiveStart == null).ThenByDescending(m => m.EffectiveStart).ThenBy(m => m.Display).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        var left = sensitive ? 0 : all.RemoveAll(m => m.Sensitive);
        return Entries(all, left, limit);
    }

    // A patient's diagnoses, every status, the latest onset first. Sensitive ones only when asked for, as above.
    public async Task<PatientEntries<Condition>> ConditionsAsync(Guid patientId, Guid? only, bool sensitive, int limit, CancellationToken cancellationToken) =>
        await RecordAsync(patientId, only, await ReadConditionsAsync(patientId, only, sensitive, limit, cancellationToken), sensitive, cancellationToken);

    async Task<PatientEntries<Condition>> ReadConditionsAsync(Guid patientId, Guid? only, bool sensitive, int limit, CancellationToken cancellationToken)
    {
        var all = await db.Conditions.AsNoTracking().Where(c => c.PatientId == patientId && (only == null || c.Id == only))
            .OrderBy(c => c.OnsetDate == null).ThenByDescending(c => c.OnsetDate).ThenBy(c => c.Display).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var left = sensitive ? 0 : all.RemoveAll(c => c.Sensitive);
        return Entries(all, left, limit);
    }

    // A patient's test results and vital signs, or those of one category, the latest taken first, up to the limit, with
    // how many there are in all; none entered in error. A blood pressure is one entry, its reading (step 9c): the
    // systolic value, with the diastolic of the same reading beside it, paired here by the reading's ID, never by time;
    // so the limit and the total count a reading once. The database holds a reading to one standing systolic and one
    // standing diastolic, taken at one time, or both in error (75-results.sql). What makes a value a reading's is its
    // reading ID, not its code: a laboratory row may carry a blood pressure code with no reading (ck_observations_reading
    // ties the two codes to readings among vital signs only), and is an entry like any other. Each value says whether its
    // unit is its code's UCUM unit.
    public async Task<PatientEntries<ObservationEntry>> ObservationsAsync(
        Guid patientId, Guid? only, string? category, int limit, CancellationToken cancellationToken) =>
        await RecordAsync(patientId, only, await ReadObservationsAsync(patientId, only, category, released: false, limit, cancellationToken), sensitive: false, cancellationToken);

    // `released`: only those released as the portal's results page has them (ResultsAsync), for the portal's download.
    async Task<PatientEntries<ObservationEntry>> ReadObservationsAsync(
        Guid patientId, Guid? only, string? category, bool released, int limit, CancellationToken cancellationToken)
    {
        var matching = (only is { } id ? ObservationById(id) : db.Observations).AsNoTracking()
            .Where(o => o.PatientId == patientId && (category == null || o.Category == category) && o.Status != ObservationStatus.EnteredInError);
        if (released)
        {
            matching = matching.Where(o => o.IssuedAt != null && ObservationStatus.Released.Contains(o.Status));
        }
        // A search reads the patient's rows alone, by their index, both values of a reading among them, and twice the
        // limit of them, since an entry is one row, or two for a reading. A reading's diastolic comes before its systolic
        // (same time, and its code sorts first), so the rows read never hold a reading's systolic without its diastolic,
        // and only a diastolic of the last moment read can be there without its systolic. A moment has one standing
        // reading (75-results.sql), so the diastolics read are at most one more than the systolics, and of 2 x limit rows
        // at least the limit are entries. Nothing is joined: a join to the diastolics, on a key no index answered, read
        // all of them for each request (step 11's benchmark).
        var rows = await (only is null ? matching.OrderByDescending(o => o.EffectiveAt).ThenBy(o => o.Code).ThenBy(o => o.Id).Take(2 * limit) : matching)
            .Select(o => new { Value = o, Ucum = db.ObservationCodes.Any(c => c.Code == o.Code && c.Unit == o.Unit) })
            .ToListAsync(cancellationToken);
        var diastolics = rows.Where(row => row.Value.ReadingId is not null && row.Value.Code == VitalSign.Diastolic)
            .ToDictionary(row => row.Value.ReadingId!.Value, row => new ObservationEntry(row.Value, row.Ucum));
        var all = rows.Where(row => row.Value.ReadingId is null || row.Value.Code != VitalSign.Diastolic)
            .Select(row => new ObservationEntry(row.Value, row.Ucum)
            {
                // Only a diastolic standing as its systolic stands: the database keeps a reading whole, and this keeps
                // the read from ever pairing a systolic with a diastolic in another state.
                Diastolic = row.Value.ReadingId is { } reading && diastolics.TryGetValue(reading, out var diastolic)
                    && diastolic.Observation.Status == row.Value.Status ? diastolic : null,
            })
            // A reading without both its values is left out here, so nothing is recorded as read that the API can't send.
            .Where(entry => entry.Observation.ReadingId is null || entry.Diastolic is not null)
            .ToList();
        // How many there are in all is asked for only when the limit may have cut the rows: never for one entry read by
        // its ID, which is all there can be. Each row but a reading's diastolic is an entry, so the count is of one table.
        var total = only is not null || rows.Count < 2 * limit ? all.Count
            : await matching.CountAsync(o => o.ReadingId == null || o.Code != VitalSign.Diastolic, cancellationToken);
        return new PatientEntries<ObservationEntry>([.. all.Take(limit)], total);
    }

    // An observation by its ID, or a blood pressure reading's two values by the reading's ID (step 9c); never a value of
    // a reading by its own ID, which is not an entry. Each part looks up a unique key, the primary key or
    // ux_observations_reading's (reading_id, code), which the planner costs as one row whatever its statistics say,
    // rather than one condition across the two columns, whose plan is the planner's to choose.
    IQueryable<Observation> ObservationById(Guid id) =>
        db.Observations.Where(o => o.Id == id && o.ReadingId == null)
            .Concat(db.Observations.Where(o => o.ReadingId == id && o.Code == VitalSign.Systolic))
            .Concat(db.Observations.Where(o => o.ReadingId == id && o.Code == VitalSign.Diastolic));

    // A patient's appointments, every status, the latest first, up to the limit, with how many there are in all.
    public async Task<PatientEntries<AppointmentEntry>> AllAppointmentsAsync(Guid patientId, Guid? only, int limit, CancellationToken cancellationToken) =>
        await RecordAsync(patientId, only, await ReadAppointmentsAsync(patientId, only, limit, cancellationToken), sensitive: false, cancellationToken);

    async Task<PatientEntries<AppointmentEntry>> ReadAppointmentsAsync(Guid patientId, Guid? only, int limit, CancellationToken cancellationToken)
    {
        var matching = db.Appointments.Where(a => a.PatientId == patientId && (only == null || a.Id == only));
        var entries = await AppointmentEntriesAsync(matching.OrderByDescending(a => a.StartsAt).ThenBy(a => a.Id).Take(limit), latestFirst: true, cancellationToken);
        var total = only is not null || entries.Count < limit ? entries.Count : await matching.CountAsync(cancellationToken);
        return new PatientEntries<AppointmentEntry>(entries, total);
    }

    // Entries read whole: the total before the limit, then the limit.
    static PatientEntries<T> Entries<T>(List<T> all, int sensitiveLeftOut, int limit) =>
        new([.. all.Take(limit)], all.Count) { SensitiveLeftOut = sensitiveLeftOut };

    // The portal's download of a patient's record (the expansion plan's step 13b): what the record holds that the portal
    // may hand its subject, read as the FHIR API reads it, with its limit for each kind of entry: their allergies, every
    // status; medications, every status but entered in error, and diagnoses, every status, sensitive ones only when asked
    // for; test results and vital signs once released, as the portal's results page has them (the rule
    // observations_own_released_select holds, asked here too, as ResultsAsync asks it), each kind read apart so neither
    // crowds out the other; and appointments, every status. Row-level security returns the record the session is looking
    // at and only what its rule allows: a proxy's sensitive items only when the patient allowed them.

    // For the file: one recorded download of the patient, apart from a view since the file leaves the portal, or a
    // download of their sensitive items when they were asked for. Null when the session may read no such patient.
    public async Task<PatientRecord?> DownloadRecordAsync(Guid patientId, bool sensitive, int limit, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var record = await ReadRecordAsync(patient, sensitive, limit, cancellationToken);
        audit.Record(sensitive ? AuditActions.DownloadSensitive : AuditActions.Download, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    // For the printable page, which is a view: the record with its visit summaries, read by VisitSummariesAsync as the
    // portal's summaries page reads them, which records the view as that page does, a read of the patient (of their
    // sensitive items when asked for) and a read of each summary shown. Null when the session may read no such patient.
    public async Task<PatientRecord?> PrintableRecordAsync(Guid patientId, bool sensitive, int limit, int summaries, CancellationToken cancellationToken) =>
        await VisitSummariesAsync(patientId, sensitive, summaries, cancellationToken) is { } read
            ? await ReadRecordAsync(read.Patient, sensitive, limit, cancellationToken) with { Summaries = read }
            : null;

    async Task<PatientRecord> ReadRecordAsync(Patient patient, bool sensitive, int limit, CancellationToken cancellationToken) => new(
        patient,
        await ReadAllergiesAsync(patient.Id, only: null, limit, cancellationToken),
        await ReadMedicationsAsync(patient.Id, only: null, sensitive, limit, cancellationToken),
        await ReadConditionsAsync(patient.Id, only: null, sensitive, limit, cancellationToken),
        await ReadObservationsAsync(patient.Id, only: null, ObservationCategory.Laboratory, released: true, limit, cancellationToken),
        await ReadObservationsAsync(patient.Id, only: null, ObservationCategory.VitalSigns, released: true, limit, cancellationToken),
        await ReadAppointmentsAsync(patient.Id, only: null, limit, cancellationToken))
    {
        SensitiveShown = sensitive,
    };

    // Refill requests (the expansion plan's step 13d).

    // The portal's refill page for the record the session is looking at: the medications it may ask to renew (active and
    // coded as a product the clinic orders, as row-level security lets the portal read them: none entered in error, a
    // sensitive one only where the patient's rule allows), but those with a request waiting; its requests, every waiting
    // one and the latest answered ones up to the limit, latest first, as the database gives the portal them
    // (ehr.portal_refill_requests: the portal reads no row of the table), so which medications wait never depends on the
    // limit; and whether anyone on the patient's panel could answer one. Sensitive medications and requests are left out
    // unless asked for, and asking is recorded as a view of the patient's sensitive items, in place of the plain read:
    // one recorded read of the patient. Null when the session may read no such patient.
    public async Task<PatientRefills?> RefillsAsync(Guid patientId, bool sensitive, int limit, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var requests = await RefillRequestQueries.PortalAsync(db, limit, cancellationToken);
        var waiting = requests.Where(r => r.Status == RefillRequestStatus.Requested).Select(r => r.MedicationStatementId).ToHashSet();
        // Coded as a product the clinic orders, by the catalog's key: one coded as an ingredient, or not coded, isn't
        // renewed here (77-refill-requests.sql).
        var medications = (await db.MedicationStatements.AsNoTracking()
            .Where(m => m.PatientId == patientId && m.Status == MedicationStatementStatus.Active && m.Code != null
                && db.OrderCatalogItems.Any(i => i.Code == m.Code && i.Type == OrderType.Medication))
            .OrderBy(m => m.Display).ThenBy(m => m.Id)
            .Select(m => new RenewableMedication(m.Id, m.Display, m.Dosage, m.Sensitive)).ToListAsync(cancellationToken))
            .Where(m => !waiting.Contains(m.Id)).ToList();
        // Disabled is locked out for good (80-appointments.sql's test): a clinician locked for a while after failed sign-ins
        // is on the team still.
        var disabled = DateTimeOffset.UtcNow.AddYears(10);
        var careTeam = await (
            from assignment in db.PatientAssignments
            join staff in db.StaffUsers on assignment.ClinicianId equals staff.Id
            where assignment.PatientId == patientId && assignment.EndedAt == null && staff.Roles.Contains(Ehr.Data.Accounts.StaffRoles.Clinician)
                && (staff.LockoutEnd == null || staff.LockoutEnd < disabled)
            select assignment.Id).AnyAsync(cancellationToken);
        var hidden = sensitive ? 0 : medications.Count(m => m.Sensitive) + requests.Count(r => r.Sensitive);

        audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientRefills(patient, sensitive ? medications : [.. medications.Where(m => !m.Sensitive)],
            sensitive ? requests : [.. requests.Where(r => !r.Sensitive)], hidden, sensitive, careTeam);
    }

    // A refill request of the patient's for the clinician's page that answers it: one recorded read of the patient, of
    // their sensitive items when the request is sensitive, since the page names its medication. Null when it isn't this
    // patient's or the reader's to see.
    public async Task<RefillReview?> RefillRequestAsync(Guid patientId, Guid requestId, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == patientId, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var allergies = await CurrentAllergies(patientId).ToListAsync(cancellationToken);
        if (await ReadRefillAsync(patient, allergies, requestId, cancellationToken) is not { } request)
        {
            return null;
        }

        audit.Record(request.Sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    // How many of a patient's refill requests were answered in the last day, the one just answered included: whether
    // the patient has already been told today. A number, about no request in particular: not a recorded read.
    public Task<int> RefillAnswersTodayAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        return db.RefillRequests.CountAsync(r => r.PatientId == patientId && r.DecidedAt > since, cancellationToken);
    }

    // A request of the patient's with its medication, who asked and who answered, and the catalog item for its
    // medication: recording nothing. Who asked is the patient by name, or someone they share their record with as
    // "Name (proxy)", as a conversation names them (ThreadAsync).
    async Task<RefillReview?> ReadRefillAsync(Patient patient, List<AllergyIntolerance> allergies, Guid requestId, CancellationToken cancellationToken)
    {
        // One statement: the request, its medication, the account that asked, who answered, and the catalog's item.
        var row = await (
            from request in db.RefillRequests.AsNoTracking()
            join medication in db.MedicationStatements on request.MedicationStatementId equals medication.Id
            join asker in db.PatientUsers on request.RequestedBy equals asker.Id
            where request.Id == requestId && request.PatientId == patient.Id
            select new
            {
                Request = request,
                Medication = new RefillMedication(medication.Display, medication.Dosage, medication.Status, medication.Code, medication.Sensitive),
                AskerPatient = asker.PatientId,
                AskerName = asker.DisplayName,
                DecidedBy = db.StaffUsers.Where(u => u.Id == request.DecidedBy).Select(u => u.DisplayName).FirstOrDefault(),
                // The same product, by its own code: the catalog's key.
                Item = db.OrderCatalogItems.Where(i => i.Code == medication.Code && i.Type == OrderType.Medication).Select(i => i.Display).FirstOrDefault(),
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var askedBy = $"{patient.GivenName} {patient.FamilyName}";
        if (row.AskerPatient != patient.Id)
        {
            // A proxy who is a patient here is named by their own record; a proxy-only account by the name it gave.
            var name = row.AskerPatient is { } own
                ? await db.Patients.AsNoTracking().Where(p => p.Id == own).Select(p => p.GivenName + " " + p.FamilyName).SingleOrDefaultAsync(cancellationToken)
                : row.AskerName;
            askedBy = $"{(string.IsNullOrWhiteSpace(name) ? "Someone" : name)} (proxy)";
        }

        var r = row.Request;
        return new RefillReview(patient, allergies, r.Id, r.MedicationDisplay, r.Note, askedBy, r.RequestedAt, r.Status, r.OrderedDisplay, r.DeclineMessage,
            row.DecidedBy, r.DecidedAt, r.Sensitive, row.Medication, row.Item);
    }

    // One view of one patient, one event; nothing for a read by ID that found nothing.
    async Task<PatientEntries<T>> RecordAsync<T>(Guid patientId, Guid? only, PatientEntries<T> read, bool sensitive, CancellationToken cancellationToken)
    {
        if (only is null || read.Entries.Count > 0)
        {
            audit.Record(sensitive ? AuditActions.ReadSensitive : AuditActions.Read, patientId, PatientsTable, patientId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return read;
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
