using System.Globalization;
using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The read-only FHIR API (the expansion plan's step 11): what the UC 19 test cases share. A patient whose record holds
// one of each thing the API serves, put there the way the clinic puts it (vital signs and appointments through their
// pages, the rest as a seed would), and the requests a FHIR client makes, with what every answer must be: FHIR JSON,
// never cached, and nothing for another site to read across origins.
static class FhirRequests
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A clinician signed in, with the patient on their panel, and the IDs of what the patient's record holds.
    public sealed record Chart(StaffAccount Clinician, Session Session, Guid Patient, string Mrn)
    {
        public Guid Penicillin { get; init; }
        public Guid Peanut { get; init; }
        public Guid Lisinopril { get; init; }
        public Guid Sertraline { get; init; }
        public Guid Hypertension { get; init; }
        public Guid Anxiety { get; init; }
        public Guid HemoglobinA1c { get; init; }
        public Guid Thyrotropin { get; init; }
        // The set of vital signs, by LOINC code, and when it was taken ("yyyy-MM-ddTHH:mm:ss", UTC).
        public IReadOnlyDictionary<string, Guid> Vitals { get; init; } = new Dictionary<string, Guid>();
        public string VitalsAt { get; init; } = "";
        public Guid Booked { get; init; }
        public Guid Cancelled { get; init; }
    }

    // The patient's record:
    // - allergies: penicillin G (RxNorm 7980) and peanut (UNII QE1QX6B99R), each with a reaction;
    // - medications: lisinopril (RxNorm 314076), and sertraline (RxNorm 312941) marked sensitive;
    // - diagnoses: hypertension (ICD-10-CM I10), and generalized anxiety disorder (F41.1), sensitive by its code;
    // - results: hemoglobin A1c (LOINC 4548-4, in %, UCUM), and thyrotropin (3016-3) in "mIU/L", which isn't UCUM;
    // - vital signs, recorded on the vital signs page: blood pressure, heart rate, temperature, respiratory rate, oxygen
    //   saturation, weight and height, and the body mass index the database works out;
    // - appointments, booked on the schedule's form: one booked, one cancelled with a reason.
    public static async Task<Chart> ChartAsync(WebApps apps, string clinicianName)
    {
        var clinician = await apps.CreateStaffAsync(clinicianName, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Fhirchart");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        const string allergy = """
            INSERT INTO ehr.allergy_intolerances (id, patient_id, code, code_system, display, clinical_status, category, criticality, reaction, severity, recorded_at)
            VALUES (@id, @patient, @code, @system, @display, 'active', @category, 'high', @reaction, @severity, '2025-03-04T10:00:00Z')
            """;
        const string medication = """
            INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, effective_start, sensitive)
            VALUES (@id, @patient, @code, @display, '1 tablet by mouth daily', 'active', '2024-01-05', @sensitive)
            """;
        const string condition = """
            INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status, onset_date)
            VALUES (@id, @patient, @code, @display, 'active', '2021-05-01')
            """;

        var taken = DateTime.UtcNow.AddMinutes(-30);
        using (var recorded = await VitalTests.RecordAtAsync(session.Client, patient, taken.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            (VitalSign.Systolic, "128"), (VitalSign.Diastolic, "82"), (VitalSign.HeartRate, "72"), (VitalSign.Temperature, "36.8"),
            (VitalSign.RespiratoryRate, "16"), (VitalSign.OxygenSaturation, "97"), (VitalSign.Weight, "70.5"), (VitalSign.Height, "172")))
        {
            Assert.Equal(VitalTests.Path(patient), PathOf(recorded));
        }

        var vitals = (await ProxyTests.QueryAsync(apps, "SELECT code || ' ' || id FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs'", patient))
            .Select(row => row.Split(' ')).ToDictionary(row => row[0], row => Guid.Parse(row[1]));
        Assert.Equal(9, vitals.Count);

        var day = AppointmentTests.Day(0);
        foreach (var (time, reason) in new[] { ("09:30", "Synthetic: blood pressure check"), ("11:00", "Synthetic: medication review") })
        {
            using var booked = await AppointmentTests.BookAsync(session.Client, patient, clinician.Id, day, time, reason);
        }

        var appointments = (await AppointmentTests.RowsAsync(apps, patient, "id::text")).Select(Guid.Parse).ToList();
        Assert.Equal(2, appointments.Count);
        using (var cancelled = await AppointmentTests.ChangeAsync(session.Client, appointments[1], "Cancel", ("CancelReason", "Synthetic: the patient asked to move it")))
        {
            Assert.Equal(["booked", "cancelled"], await AppointmentTests.RowsAsync(apps, patient));
        }

        var effective = DateTimeOffset.UtcNow.AddDays(-3);
        return new Chart(clinician, session, patient, WebApps.MrnOf(patient))
        {
            Penicillin = await InsertAsync(apps, allergy, patient, ("code", "7980"), ("system", "rxnorm"), ("display", "Penicillin G"), ("category", "medication"),
                ("reaction", "Hives"), ("severity", "moderate")),
            Peanut = await InsertAsync(apps, allergy, patient, ("code", "QE1QX6B99R"), ("system", "unii"), ("display", "Peanut"), ("category", "food"),
                ("reaction", "Anaphylaxis"), ("severity", "severe")),
            Lisinopril = await InsertAsync(apps, medication, patient, ("code", "314076"), ("display", "lisinopril 10 MG Oral Tablet"), ("sensitive", false)),
            Sertraline = await InsertAsync(apps, medication, patient, ("code", "312941"), ("display", "sertraline 50 MG Oral Tablet"), ("sensitive", true)),
            Hypertension = await InsertAsync(apps, condition, patient, ("code", "I10"), ("display", "Essential (primary) hypertension")),
            Anxiety = await InsertAsync(apps, condition, patient, ("code", "F41.1"), ("display", "Generalized anxiety disorder")),
            HemoglobinA1c = await apps.InsertResultAsync(patient, "Hemoglobin A1c", "4548-4", 6.9m, "%", 4.0m, 5.6m, effective, effective,
                note: "Synthetic: we'll talk about this at your next visit."),
            Thyrotropin = await apps.InsertResultAsync(patient, "Thyroid stimulating hormone", "3016-3", 2.1m, "mIU/L", 0.4m, 4.0m, effective, effective),
            Vitals = vitals,
            VitalsAt = new DateTime(taken.Year, taken.Month, taken.Day, taken.Hour, taken.Minute, 0).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Booked = appointments[0],
            Cancelled = appointments[1],
        };
    }

    // A row of the patient's record, inserted as the superuser the way a seed would.
    public static async Task<Guid> InsertAsync(WebApps apps, string sql, Guid patient, params (string Name, object Value)[] values)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("patient", patient);
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Cancel);
        return id;
    }

    // What came back: the status, the body, and the headers, those of the content included.
    public sealed record Answer(HttpStatusCode Status, string Json, string? ContentType, string? CacheControl, string? Allow, IReadOnlyList<string> Headers);

    public static Task<Answer> GetAsync(HttpClient client, string path, params (string Name, string Value)[] headers) =>
        SendAsync(client, HttpMethod.Get, path, headers);

    // A request as a FHIR client makes it, asking for FHIR JSON.
    public static async Task<Answer> SendAsync(HttpClient client, HttpMethod method, string path, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.ParseAdd("application/fhir+json");
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Delete && method != HttpMethod.Options)
        {
            request.Content = new StringContent("""{"resourceType":"Patient"}""", System.Text.Encoding.UTF8, "application/fhir+json");
        }

        using var response = await client.SendAsync(request, Cancel);
        return new Answer(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(Cancel),
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.CacheControl?.ToString(),
            response.Content.Headers.Allow.Count > 0 ? string.Join(", ", response.Content.Headers.Allow) : null,
            [.. response.Headers.Select(header => header.Key), .. response.Content.Headers.Select(header => header.Key)]);
    }

    // What every answer of the API is, whatever it says: FHIR JSON, never kept by a browser or a proxy, never a
    // redirect, and with nothing that lets a page on another site read it.
    public static Answer Expect(Answer answer, HttpStatusCode status)
    {
        Assert.True(answer.Status == status, $"Expected {(int)status}, got {(int)answer.Status}: {answer.Json}");
        Assert.Equal(ContentType, answer.ContentType);
        // no-store, which a failure's answer has beside no-cache (ASP.NET Core's exception handler adds that).
        Assert.Contains("no-store", (answer.CacheControl ?? "").Split(", "));
        Assert.DoesNotContain(answer.Headers, header => header.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Location", answer.Headers);
        return answer;
    }

    // A refusal or failure: an OperationOutcome, valid R4, with one error of this code.
    public static Hl7.Fhir.Model.OperationOutcome Refused(Answer answer, HttpStatusCode status, string code)
    {
        var outcome = FhirConformance.Valid<Hl7.Fhir.Model.OperationOutcome>(Expect(answer, status).Json);
        var issue = Assert.Single(outcome.Issue);
        Assert.Equal(Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error, issue.Severity);
        Assert.Equal(code, Hl7.Fhir.Utility.EnumUtility.GetLiteral(issue.Code));
        return outcome;
    }

    // A search's answer: a valid searchset Bundle, with the resources it matched and what it said beside them.
    public static (Hl7.Fhir.Model.Bundle Bundle, List<T> Matches, List<string> Notes) Searched<T>(Answer answer) where T : Hl7.Fhir.Model.Resource
    {
        var bundle = FhirConformance.Valid<Hl7.Fhir.Model.Bundle>(Expect(answer, HttpStatusCode.OK).Json);
        Assert.Equal(Hl7.Fhir.Model.Bundle.BundleType.Searchset, bundle.Type);
        var matches = bundle.Entry.Where(entry => entry.Search?.Mode == Hl7.Fhir.Model.Bundle.SearchEntryMode.Match).ToList();
        Assert.All(matches, entry => Assert.Equal($"https://localhost/fhir/{entry.Resource?.TypeName}/{entry.Resource?.Id}", entry.FullUrl));
        var notes = bundle.Entry.Where(entry => entry.Search?.Mode == Hl7.Fhir.Model.Bundle.SearchEntryMode.Outcome)
            .SelectMany(entry => Assert.IsType<Hl7.Fhir.Model.OperationOutcome>(entry.Resource).Issue).Select(issue => issue.Diagnostics ?? "").ToList();
        return (bundle, [.. matches.Select(entry => Assert.IsAssignableFrom<T>(entry.Resource))], notes);
    }

    // How many reads, and reads of sensitive items, the clinician has had recorded about the patient.
    public static async Task<(long Reads, long Sensitive)> ReadsAsync(WebApps apps, Guid clinician, Guid patient) =>
        (await apps.AuditEventsAsync(Ehr.Data.Audit.AuditActions.Read, clinician, patient),
            await apps.AuditEventsAsync(Ehr.Data.Audit.AuditActions.ReadSensitive, clinician, patient));
}
