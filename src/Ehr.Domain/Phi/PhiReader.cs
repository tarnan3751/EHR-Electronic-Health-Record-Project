using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Ehr.Data.Scheduling;
using Ehr.Domain.Audit;
using Ehr.Domain.Orders;
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
        return new PatientChart(patient, allergies, medications, conditions, encounters, orders)
        {
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
                appointment.CancelReason)).ToListAsync(cancellationToken);
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
        var ordered = done ? mine.OrderByDescending(i => i.DoneAt).ThenBy(i => i.Id) : mine.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id);
        var items = await (
            from item in ordered.Take(limit + 1)
            join patient in db.Patients on item.PatientId equals patient.Id into patients
            from patient in patients.DefaultIfEmpty()
            select new InboxEntry(item.Id, item.Kind, item.Summary, item.CreatedAt, item.DoneAt, item.PatientId,
                patient == null ? null : patient.FamilyName + ", " + patient.GivenName, patient == null ? null : patient.Mrn, item.SourceTable, item.SourceId))
            .ToListAsync(cancellationToken);
        items = [.. done ? items.OrderByDescending(i => i.DoneAt).ThenBy(i => i.Id) : items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)];
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
