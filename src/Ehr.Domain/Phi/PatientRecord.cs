using Ehr.Data.Patients;
using Ehr.Domain.Scheduling;

namespace Ehr.Domain.Phi;

// A patient's record as the portal hands it over (the expansion plan's step 13b), already recorded: the download's file,
// or its printable page (PhiReader.DownloadRecordAsync and PrintableRecordAsync). Each kind of entry newest first by its
// own date, as the FHIR API orders them, up to the limit, with how many there are in all. A blood pressure is one entry,
// its reading.
public sealed record PatientRecord(
    Patient Patient,
    PatientEntries<AllergyIntolerance> Allergies,
    PatientEntries<MedicationStatement> Medications,
    PatientEntries<Condition> Conditions,
    PatientEntries<ObservationEntry> Results,
    PatientEntries<ObservationEntry> VitalSigns,
    PatientEntries<AppointmentEntry> Appointments)
{
    // The printable page's visit summaries, as the portal's summaries page has them; none for the file.
    public PatientVisitSummaries? Summaries { get; init; }

    // Whether sensitive diagnoses and medications were asked for, and how many were left out when they weren't.
    public bool SensitiveShown { get; init; }

    public int SensitiveHidden => Medications.SensitiveLeftOut + Conditions.SensitiveLeftOut;
}
