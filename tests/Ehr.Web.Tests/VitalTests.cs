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

    // "Record vital signs" with the values typed, taken so many minutes ago.
    public static Task<HttpResponseMessage> RecordAsync(HttpClient client, Guid patient, int minutesAgo, params (string Code, string Value)[] values) =>
        RecordAtAsync(client, patient, DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm"), values);

    public static Task<HttpResponseMessage> RecordAtAsync(HttpClient client, Guid patient, string takenAt, params (string Code, string Value)[] values) =>
        PostFormAsync(client, Path(patient), Path(patient), [.. values.Select(v => ($"Values[{v.Code}]", (object)v.Value)), ("TakenAt", takenAt)]);

    // "code value" for each vital sign of the patient, oldest first, then by code.
    public static Task<List<string>> RecordedAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        "SELECT code || ' ' || trim_scale(value) FROM ehr.observations WHERE patient_id = @patient AND category = 'vital-signs' ORDER BY effective_at, code", patient);
}
