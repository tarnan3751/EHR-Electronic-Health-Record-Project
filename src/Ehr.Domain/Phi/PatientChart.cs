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

// The vital signs a visit shows on the chart; null where one wasn't taken.
public sealed record VisitVitals(decimal? Systolic, decimal? Diastolic, decimal? HeartRate);

// An addendum to a signed note as a page shows it.
public sealed record AddendumEntry(Guid Id, string Text, DateTimeOffset SignedAt, bool Sensitive, Guid AuthorId, string AuthorName);

// A note's addenda in the order they were signed: the ones shown, how many sensitive ones were left out, and whether
// sensitive ones were shown.
public sealed record NoteAddenda(IReadOnlyList<AddendumEntry> Shown, int SensitiveHidden, bool SensitiveShown);

// A signed note for the offline copy: the note, who signed it, and its addenda.
public sealed record OfflineNote(ClinicalNote Note, string SignerName, IReadOnlyList<AddendumEntry> Addenda);

// A vital sign's newest value, for the chart's short flowsheet.
public sealed record LatestVital(string Code, string Display, decimal Value, string? Unit, decimal? ReferenceLow, decimal? ReferenceHigh, DateTimeOffset TakenAt);

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
}

// UC 1: a patient's discontinued orders as their page lists them, already recorded as one read of the patient. More is
// set when there are more than the page asked for.
public sealed record DiscontinuedOrders(Patient Patient, IReadOnlyList<DiscontinuedOrder> Orders, bool More);
