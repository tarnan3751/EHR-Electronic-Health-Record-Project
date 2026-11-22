using Ehr.Data.Patients;
using Ehr.Domain.Orders;

namespace Ehr.Domain.Phi;

// A patient's chart as PhiReader.ChartAsync returns it, already recorded as one read of the patient: the allergies,
// medications and diagnoses the patient has now, their latest visits, newest first, and their active orders (UC 1),
// newest first.
public sealed record PatientChart(
    Patient Patient,
    IReadOnlyList<AllergyIntolerance> Allergies,
    IReadOnlyList<MedicationStatement> Medications,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<Encounter> Visits,
    IReadOnlyList<ActiveOrder> Orders)
{
    // UC 1: set when the chart was asked to confirm a discontinuation and that order is this patient's and
    // discontinued. Part of the same recorded read.
    public bool ConfirmsDiscontinued { get; init; }

    // Sensitive diagnoses and medications are left out unless asked for: how many were left out, and whether they
    // were asked for (and so are in the lists, and the asking was recorded).
    public int SensitiveHidden { get; init; }

    public bool SensitiveShown { get; init; }

    // The patient's latest conversations with their care team (FR-18), with how many of the patient's messages are
    // unread: part of the same recorded read.
    public IReadOnlyList<Ehr.Domain.Messaging.ThreadEntry> Threads { get; init; } = [];

    // The patient's appointments still to come, soonest first (FR-17): part of the same recorded read.
    public IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Upcoming { get; init; } = [];

    // The blood pressure and heart rate taken at each visit shown, by the visit's ID (step 9).
    public IReadOnlyDictionary<Guid, VisitVitals> VisitVitals { get; init; } = new Dictionary<Guid, VisitVitals>();

    // The patient's signed notes for the chart's list, newest first: sensitive ones only when sensitive items are shown.
    public IReadOnlyList<SignedNoteEntry> Notes { get; init; } = [];

    // Signed notes marked sensitive that the chart's list leaves out (step 10); zero when sensitive items are shown.
    public int SensitiveNotesHidden { get; init; }

    // For the desktop app's offline copy only: the latest signed notes that aren't sensitive, with their addenda.
    public IReadOnlyList<OfflineNote> OfflineNotes { get; init; } = [];

    // The newest value of each vital sign among the patient's latest sets, in the flowsheet's order (step 9).
    public IReadOnlyList<LatestVital> LatestVitals { get; init; } = [];

    // Whether the patient has any signed note, and any draft the reader may see: the page asks for those lists
    // only when there is something in them.
    public bool HasNotes { get; init; } = true;

    public bool HasDrafts { get; init; } = true;

    // Lab orders whose results are in (step 7): those still to acknowledge first, abnormal ones before the rest.
    public IReadOnlyList<Ehr.Domain.Results.ResultedOrder> Resulted { get; init; } = [];
}

// The vital signs a visit shows on the chart; null where one wasn't taken. The blood pressure is one reading's two
// numbers (step 9c).
public sealed record VisitVitals(decimal? Systolic, decimal? Diastolic, decimal? HeartRate)
{
    public static VisitVitals Of(Ehr.Domain.Results.BloodPressure? pressure, decimal? heartRate) =>
        new(pressure?.Systolic.Value, pressure?.Diastolic.Value, heartRate);
}

// A signed note as the chart lists it: when it was signed and whether it is sensitive. Not its text.
public sealed record SignedNoteEntry(Guid Id, DateTimeOffset SignedAt, bool Sensitive)
{
    // Whether a visit summary written from it stands in the portal (step 13a).
    public bool SummaryReleased { get; init; }
}

// A visit summary as a page shows it (step 13a): its three parts, when the note it was written from was signed, who
// released it and when, whether it is sensitive and how, and, once withdrawn, who withdrew it, when and why.
public sealed record VisitSummaryEntry(
    Guid Id, string TalkedAbout, string Found, string NextSteps, DateTimeOffset NoteSignedAt, string ReleasedBy, DateTimeOffset ReleasedAt, bool Sensitive)
{
    public Guid ReleasedById { get; init; }

    // Sensitive only because the note or one of its addenda is: the mark goes with theirs.
    public bool SensitiveInherited { get; init; }

    // Who made it sensitive, and why and when its sensitivity last changed after it was released.
    public Guid? SensitiveMarkedBy { get; init; }

    public string? SensitiveReason { get; init; }

    public DateTimeOffset? SensitiveChangedAt { get; init; }

    public DateTimeOffset? WithdrawnAt { get; init; }

    public string? WithdrawnBy { get; init; }

    public string? WithdrawnReason { get; init; }
}

// A patient's visit summaries as the portal lists them (step 13a), already recorded as one read of the patient and a
// read of each summary shown: the standing ones, newest note first, as the portal is given them (no marks, reasons or
// withdrawals), and the withdrawn ones, as their dates alone; how many sensitive ones were left out, and whether
// sensitive ones were asked for.
public sealed record PatientVisitSummaries(
    Patient Patient, IReadOnlyList<Ehr.Data.Patients.PortalVisitSummary> Summaries, IReadOnlyList<Ehr.Data.Patients.WithdrawnVisitSummary> Withdrawn, int SensitiveHidden, bool SensitiveShown);

// A note's visit summaries as its page shows them: the standing one first, then those withdrawn; how many sensitive ones
// were left out, and whether sensitive ones were shown.
public sealed record NoteSummaries(IReadOnlyList<VisitSummaryEntry> Shown, int SensitiveHidden, bool SensitiveShown)
{
    // Whether one stands in the portal, shown here or not: then no other is offered.
    public bool HasStanding { get; init; }
}

// An addendum to a signed note as a page shows it.
public sealed record AddendumEntry(Guid Id, string Text, DateTimeOffset SignedAt, bool Sensitive, Guid AuthorId, string AuthorName)
{
    // Whether its author is the note's signer (step 10b): one that isn't is labeled as not the note's author.
    public bool ByNoteAuthor { get; init; } = true;

    // Sensitive only because its note is: the mark came from the note and goes with it.
    public bool SensitiveInherited { get; init; }

    // Who made it sensitive, and why and when its sensitivity last changed after it was signed.
    public Guid? SensitiveMarkedBy { get; init; }

    public string? SensitiveReason { get; init; }

    public DateTimeOffset? SensitiveChangedAt { get; init; }
}

// A note's addenda in the order they were signed: the ones shown, how many sensitive ones were left out, and whether
// sensitive ones were shown.
public sealed record NoteAddenda(IReadOnlyList<AddendumEntry> Shown, int SensitiveHidden, bool SensitiveShown)
{
    // Every one, hidden ones too: for the reader's own use, never for a page to list.
    internal IReadOnlyList<AddendumEntry> All { get; init; } = [];
}

// A signed note for the offline copy: the note, who signed it, and its addenda.
public sealed record OfflineNote(ClinicalNote Note, string SignerName, IReadOnlyList<AddendumEntry> Addenda)
{
    // Sensitive addenda the note has that are not in the copy.
    public int SensitiveAddendaNotKept { get; init; }
}

// A vital sign's newest value, for the chart's short flowsheet.
public sealed record LatestVital(string Code, string Display, decimal Value, string? Unit, decimal? ReferenceLow, decimal? ReferenceHigh, DateTimeOffset TakenAt)
{
    // The reading before this one, when the chart's query held one (step 9b): for the up or down mark.
    public decimal? Previous { get; init; }

    // What its range is (ReferenceKind): a child's blood pressure is a screening value (step 9d).
    public string ReferenceKind { get; init; } = Data.Patients.ReferenceKind.Normal;

    // For a blood pressure, which is one reading (step 9c): this is its systolic, and this the diastolic of the same
    // reading, with the diastolic of the reading before.
    public LatestVital? Diastolic { get; init; }

    // Up or down from the reading before: for a blood pressure, only when its two numbers didn't move opposite ways.
    public string? Direction
    {
        get
        {
            var moves = new[] { (Value, Previous), (Diastolic?.Value, Diastolic?.Previous) }.Where(move => move.Item1 is not null && move.Item2 is not null)
                .Select(move => Math.Sign(move.Item1!.Value - move.Item2!.Value)).ToList();
            if (moves.Count == 0 || (Diastolic is not null && moves.Count < 2) || moves.Contains(1) == moves.Contains(-1))
            {
                return null;
            }

            return moves.Contains(1) ? "up" : "down";
        }
    }

    // The value as the chart shows it, and the reading before, the same way: "126/80" for a blood pressure.
    public string Shown => Diastolic is null ? $"{Value:0.#}" : $"{Value:0.#}/{Diastolic.Value:0.#}";

    public string PreviousShown => Diastolic is null ? $"{Previous:0.#}" : $"{Previous:0.#}/{Diastolic.Previous:0.#}";

    // Its flags, each once, a blood pressure's systolic first: "High", "Low" or "Needs follow-up" (VitalFlag).
    public IReadOnlyList<string> Flags => [.. new[] { Flag, Diastolic?.Flag }.OfType<string>().Distinct()];

    string? Flag => Results.VitalFlag.Of(Value, ReferenceLow, ReferenceHigh, ReferenceKind);

    // The newest blood pressure among these readings, newest first: its systolic, with the diastolic of the same
    // reading, each with the value of the reading before it.
    public static IEnumerable<LatestVital> Pressure(IReadOnlyList<Ehr.Domain.Results.BloodPressure> readings)
    {
        if (readings.Count > 0)
        {
            var (latest, before) = (readings[0], readings.Count > 1 ? readings[1] : null);
            yield return Of(latest.Systolic) with { Previous = before?.Systolic.Value, Diastolic = Of(latest.Diastolic) with { Previous = before?.Diastolic.Value } };
        }
    }

    public static LatestVital Of(Observation value) =>
        new(value.Code!, value.Display, value.Value!.Value, value.Unit, value.ReferenceLow, value.ReferenceHigh, value.EffectiveAt)
        {
            ReferenceKind = value.ReferenceKind,
        };
}

// A patient's message threads, latest first, already recorded as one read of the patient.
public sealed record PatientThreads(Patient Patient, IReadOnlyList<Ehr.Domain.Messaging.ThreadEntry> Threads, IReadOnlyList<AllergyIntolerance> Allergies);

// One thread with its messages in order, already recorded as a read of the thread. Unread are the messages from the
// other side that the reader is opening for the first time.
public sealed record ThreadDetail(
    Guid Id, string Subject, Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<Ehr.Domain.Messaging.MessageEntry> Messages,
    IReadOnlyList<Guid> Unread);

// A patient's appointments as the portal lists them, already recorded as one read of the patient: those still to come,
// soonest first, and those past or cancelled, latest first.
public sealed record PatientAppointments(
    Patient Patient, IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Upcoming, IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Past);

// One appointment with its patient, and the patient's allergies for the banner: one recorded read of the patient.
public sealed record AppointmentDetail(Ehr.Domain.Scheduling.AppointmentEntry Appointment, Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies);

// One item of a staff member's inbox as its page lists it: what it is, the patient it is about (by name for a
// clinician, who may read patients; by ID alone for an admin, who may not), and where it came from.
public sealed record InboxEntry(
    Guid Id, string Kind, string Summary, DateTimeOffset CreatedAt, DateTimeOffset? DoneAt, Guid? PatientId, string? PatientName, string? PatientMrn,
    string? SourceTable, Guid? SourceId, bool Abnormal = false);

public sealed record InboxPage(IReadOnlyList<InboxEntry> Items, bool More);

// A patient with their current allergies, for the banner of a page that isn't the chart. One recorded read.
public sealed record PatientHeader(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies);

// The form that adds or changes one entry of a patient's allergies, medications or diagnoses, already recorded as one
// read of the patient: the banner's patient and allergies, and the entry being changed, when there is one.
public sealed record ChartEntryForm(
    Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, AllergyIntolerance? Allergy = null, MedicationStatement? Medication = null,
    Condition? Condition = null);

// One change to a patient's allergies, medications or diagnoses, as the history page lists it.
public sealed record ChartChange(string TableName, Guid RowId, DateTimeOffset ChangedAt, string? ChangedBy, string? Before, string After);

public sealed record ChartHistory(Patient Patient, IReadOnlyList<ChartChange> Changes, bool More, int SensitiveHidden, bool SensitiveShown);

// UC 1 (Tahseen Arnan): a patient as the order form shows them, already recorded as one read of the patient: the
// banner's name and allergies, and, once an item is picked, the patient's active orders for it, for the duplicate warning.
// Replaced is the active order a change replaces, or the one being discontinued; it is never among the duplicates.
public sealed record OrderForm(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<ActiveOrder> Duplicates, ActiveOrder? Replaced)
{
    // Step 8: what the database knows against ordering the chosen item for this patient (an allergy, a class the
    // patient is allergic to, an interaction with something they take). Empty until an item is chosen.
    public IReadOnlyList<Ehr.Data.Orders.SafetyWarning> Warnings { get; init; } = [];

    // Step 13d: the refill request the order approves, when the form was opened to approve one of this patient's; and
    // whether the chosen item renews its medication: the same product, the medication's own code. False until an item is
    // chosen.
    public Ehr.Domain.Refills.RefillReview? Refill { get; init; }

    public bool RenewsRefill { get; init; }
}

// UC 1: a patient's discontinued orders as their page lists them, already recorded as one read of the patient. More is
// set when there are more than the page asked for.
public sealed record DiscontinuedOrders(Patient Patient, IReadOnlyList<DiscontinuedOrder> Orders, bool More);

// Entries of one kind for one patient, as the FHIR API reads them (the expansion plan's step 11), already recorded as
// one read of the patient. Total counts every entry that matched, of which Entries holds up to the limit asked for;
// SensitiveLeftOut counts the sensitive ones left out because they weren't asked for.
public sealed record PatientEntries<T>(IReadOnlyList<T> Entries, int Total)
{
    public int SensitiveLeftOut { get; init; }
}

// A test result or vital sign for the FHIR API, and whether its unit is the UCUM unit the reference data gives its code
// (ehr.observation_codes), which is what lets the API call it a UCUM unit.
public sealed record ObservationEntry(Observation Observation, bool UcumUnit)
{
    // For a blood pressure, whose entry is its reading (step 9c): this is the systolic value, and this the diastolic of
    // the same reading.
    public ObservationEntry? Diastolic { get; init; }
}
