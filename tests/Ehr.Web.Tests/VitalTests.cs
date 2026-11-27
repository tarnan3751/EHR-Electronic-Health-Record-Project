using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Vital signs and trends (the expansion plan's step 9): helpers for the test cases TC-UC4-14 to 17, TC-UC1-21 and
// TC-UC6-14. What the database refuses whatever the app does is in Ehr.Security.Tests (VitalSignSecurityTests).
static class VitalTests
{
    public const string Systolic = "8480-6";
    public const string Diastolic = "8462-4";
    public const string HeartRate = "8867-4";
    public const string Temperature = "8310-5";
    public const string Weight = "29463-7";
    public const string Height = "8302-2";
    public const string BodyMassIndex = "39156-5";

    public static string Path(Guid patient) => $"/patients/{patient}/vitals";

    // "Record vital signs" with the values typed, taken so many minutes ago by the database's clock, which judges the time.
    public static async Task<HttpResponseMessage> RecordAsync(WebApps apps, HttpClient client, Guid patient, int minutesAgo, params (string Code, string Value)[] values) =>
        await RecordAtAsync(client, patient, (await apps.Database.NowAsync()).AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm"), values);

    public static Task<HttpResponseMessage> RecordAtAsync(HttpClient client, Guid patient, string takenAt, params (string Code, string Value)[] values) =>
        PostFormAsync(client, Path(patient), Path(patient), [.. values.Select(v => ($"Values[{v.Code}]", (object)v.Value)), ("TakenAt", takenAt)]);

    public static string ErrorPath(Guid patient, string at) => $"/patients/{patient}/vitals/error?at={at}";

    // "Mark as entered in error" for the set taken at `at` ("yyyy-MM-ddTHH:mm:ss", UTC): the readings of these codes, as
    // the page posts them, by observation ID.
    public static async Task<HttpResponseMessage> MarkAsync(WebApps apps, HttpClient client, Guid patient, string at, string? reason, params string[] codes)
    {
        var ids = (await ProxyTests.QueryAsync(apps,
            $"SELECT code || ' ' || id FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' AND status = 'final' AND effective_at = '{at}Z'", patient))
            .Select(line => line.Split(' ')).Where(parts => codes.Contains(parts[0])).Select(parts => parts[1]);
        (string Name, object Value)[] fields = [.. ids.Select(id => ("Chosen", (object)id)), .. reason is null ? Array.Empty<(string, object)>() : [("Reason", reason)]];
        return await PostFormAsync(client, ErrorPath(patient, at), ErrorPath(patient, at), fields);
    }

    // "code value status" for each vital sign of the patient, oldest first, then by code, then as recorded.
    public static Task<List<string>> StatesAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        "SELECT code || ' ' || trim_scale(value) || ' ' || status FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' ORDER BY effective_at, code, issued_at", patient);

    // "code value" for each vital sign of the patient, oldest first, then by code.
    public static Task<List<string>> RecordedAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        "SELECT code || ' ' || trim_scale(value) FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' ORDER BY effective_at, code", patient);

    // "code value status visit reading" for each blood pressure value of the patient, in the order recorded (step 9c):
    // the visit as "visit" or "none", each reading as R1, R2... in the order readings first appear.
    public static async Task<List<string>> ReadingsAsync(WebApps apps, Guid patient)
    {
        var readings = new List<string>();
        return [.. (await ProxyTests.QueryAsync(apps,
            """
            SELECT code || ' ' || trim_scale(value) || ' ' || status || ' ' || CASE WHEN encounter_id IS NULL THEN 'none' ELSE 'visit' END || ' ' || reading_id
            FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' AND code IN ('8480-6', '8462-4') ORDER BY issued_at
            """, patient)).Select(row =>
            {
                var reading = row[(row.LastIndexOf(' ') + 1)..];
                if (!readings.Contains(reading))
                {
                    readings.Add(reading);
                }

                return $"{row[..row.LastIndexOf(' ')]} R{readings.IndexOf(reading) + 1}";
            })];
    }
}
