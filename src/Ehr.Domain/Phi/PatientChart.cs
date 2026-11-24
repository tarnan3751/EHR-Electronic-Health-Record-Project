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
    // discontinued: the order's name. Part of the same recorded read.
    public string? DiscontinuedName { get; init; }

    public bool ConfirmsDiscontinued => DiscontinuedName is not null;

    // UC 1: set when the chart was asked to confirm an order just signed that replaced another (a change): the name
    // of the order it replaced. Part of the same recorded read.
    public string? ReplacedName { get; init; }

    // Sensitive diagnoses and medications are left out unless asked for: how many were left out, and whether they
    // were asked for (and so are in the lists, and the asking was recorded).
    public int SensitiveHidden { get; init; }

    public bool SensitiveShown { get; init; }

    // The patient's latest conversations with their care team (FR-18), with how many of the patient's messages are
    // unread: part of the same recorded read.
    public IReadOnlyList<Ehr.Domain.Messaging.ThreadEntry> Threads { get; init; } = [];

    // The patient's appointments still to come, soonest first (FR-17): part of the same recorded read.
    public IReadOnlyList<Ehr.Domain.Scheduling.AppointmentEntry> Upcoming { get; init; } = [];

    // Set when the entry form sent the clinician here after saving an entry, and that entry is this patient's: what
    // the chart's "Saved:" notice says about it. Part of the same recorded read.
    public SavedEntry? Saved { get; init; }

    // When "No known allergies" is the allergy list's state: who recorded it, when the history knows (seeded entries
    // have none). Part of the same recorded read.
    public string? NoKnownAllergiesRecordedBy { get; init; }
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
    string? SourceTable, Guid? SourceId);

public sealed record InboxPage(IReadOnlyList<InboxEntry> Items, bool More);

// An entry of one of the chart's lists just saved, as the chart's notice describes it: its list (by table), name,
// status, whether it is among the lists the chart shows now (Listed), and whether the chart left it out as sensitive
// (Hidden), in which case the notice doesn't name it.
public sealed record SavedEntry(string Table, string Display, string Status, bool Listed, bool Hidden);

// A patient with their current allergies, for the banner of a page that isn't the chart. One recorded read.
public sealed record PatientHeader(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies);

// The form that adds or changes one entry of a patient's allergies, medications or diagnoses, already recorded as one
// read of the patient: the banner's patient and allergies, and the entry being changed, when there is one.
public sealed record ChartEntryForm(
    Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, AllergyIntolerance? Allergy = null, MedicationStatement? Medication = null,
    Condition? Condition = null);

// One change to a patient's allergies, medications or diagnoses, as the history page lists it.
public sealed record ChartChange(string TableName, Guid RowId, DateTimeOffset ChangedAt, string? ChangedBy, string? Before, string After);

// The history page: with the banner's allergies, read in the same recorded read.
public sealed record ChartHistory(
    Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<ChartChange> Changes, bool More, int SensitiveHidden, bool SensitiveShown);

// UC 1 (Tahseen Arnan): a patient as the order form shows them, already recorded as one read of the patient: the
// banner's name and allergies, and, once an item is picked, the patient's active orders for it, for the duplicate warning.
// Replaced is the active order a change replaces, or the one being discontinued; it is never among the duplicates.
public sealed record OrderForm(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<ActiveOrder> Duplicates, ActiveOrder? Replaced);

// UC 1: a patient's discontinued orders as their page lists them, with the banner's allergies, already recorded as one
// read of the patient. More is set when there are more than the page asked for.
public sealed record DiscontinuedOrders(Patient Patient, IReadOnlyList<AllergyIntolerance> Allergies, IReadOnlyList<DiscontinuedOrder> Orders, bool More);
