using System.Globalization;
using System.Text.Json;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ehr.Web.Fhir;

// A patient's record as one FHIR R4 file, for the portal's download (the expansion plan's step 13b): a collection Bundle
// of the patient, their allergies, medications, diagnoses, test results, vital signs and appointments, as the PHI reader
// read them for the portal (PhiReader.DownloadRecordAsync), from the same mapping as the clinician API. A collection, not
// a document: nothing is authored for it, it is the entries, put together when it was downloaded. Its ids are made for
// this file alone (FhirIds.ForDownload), so nothing in it is an identifier of the clinic's but the patient's own record
// number. An appointment's cancellation reason, which the portal never shows, is left out. A kind of entry cut by its
// limit gets an OperationOutcome entry that says so. Nothing says whether sensitive items were left out: a file passed on
// mustn't tell whoever reads it that there are any.
internal static class FhirRecord
{
    public static FhirBundle Bundle(PatientRecord record, DateTimeOffset at)
    {
        var ids = FhirIds.ForDownload();
        var entries = new List<BundleEntry>();
        void Add(Guid id, object resource) => entries.Add(new BundleEntry(ids.Address(id), resource));

        Add(record.Patient.Id, FhirResources.Patient(record.Patient, ids));
        foreach (var allergy in record.Allergies.Entries)
        {
            Add(allergy.Id, FhirResources.Allergy(allergy, ids));
        }

        foreach (var medication in record.Medications.Entries)
        {
            Add(medication.Id, FhirResources.Medication(medication, ids));
        }

        foreach (var condition in record.Conditions.Entries)
        {
            Add(condition.Id, FhirResources.Condition(condition, ids));
        }

        foreach (var (id, observation) in FhirObservations.Map([.. record.Results.Entries, .. record.VitalSigns.Entries], ids))
        {
            Add(id, observation);
        }

        foreach (var appointment in record.Appointments.Entries)
        {
            Add(appointment.Id, FhirResources.Appointment(appointment, ids) with { CancelationReason = null });
        }

        foreach (var cut in new[]
        {
            Cut(record.Allergies, "allergies"), Cut(record.Medications, "medications"), Cut(record.Conditions, "diagnoses"),
            Cut(record.Results, "test results"), Cut(record.VitalSigns, "vital signs"), Cut(record.Appointments, "appointments"),
        }.OfType<string>())
        {
            entries.Add(new BundleEntry($"urn:uuid:{Guid.NewGuid()}", new FhirOperationOutcome([new Issue("warning", "informational", cut + " are in this file.")])));
        }

        return new FhirBundle("collection", FhirResources.Instant(at), Total: null, Link: null, entries);
    }

    // What the limit cut of one kind of entry, as "Only the newest 600 of the 734 test results", or null: for the file and
    // its printable page.
    public static string? Cut<T>(PatientEntries<T> entries, string kind) => entries.Total > entries.Entries.Count
        ? string.Create(CultureInfo.InvariantCulture, $"Only the newest {entries.Entries.Count} of the {entries.Total:N0} {kind}")
        : null;

    // The file's name: what it is and the day it was downloaded, in UTC, and no one's name, record number or ID.
    public static string FileName(DateTimeOffset at) => $"health-record-{at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";
}

// The file as the browser is given it: FHIR JSON, to be saved rather than shown, never kept by a browser or a proxy. It
// is written straight to the response once the handler has returned: on-site, after the page's transaction has
// committed with the download's audit event (DatabaseTransactionFilter commits before a result runs); off-site, into
// the page the cloud app holds until its audit queue has the event (CloudAuditFlushMiddleware).
internal sealed class FhirRecordFile(FhirBundle bundle, string fileName) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        response.ContentType = FhirResources.ContentType;
        response.Headers.CacheControl = "no-store";
        response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = fileName }.ToString();
        await JsonSerializer.SerializeAsync(response.Body, bundle, FhirResources.Json, CancellationToken.None);
    }
}
