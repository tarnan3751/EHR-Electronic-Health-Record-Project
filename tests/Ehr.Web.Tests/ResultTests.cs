using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Results of orders (the expansion plan's step 7): helpers for the test cases TC-UC1-13 to 17, TC-UC6-12 and
// TC-UC15-07. What the database refuses whatever the app does is in Ehr.Security.Tests (ResultSecurityTests).
static class ResultTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // A complete blood count's tests (db/reference): hemoglobin 12.0 to 17.5 g/dL, platelets 150 to 400.
    public const string Hemoglobin = "718-7";
    public const string Platelets = "777-3";
    public const string Hematocrit = "4544-3";
    public const string Leukocytes = "6690-2";

    public static string Path(Guid patient, Guid order) => $"/patients/{patient}/orders/{order}/results";

    // "File results" with the values typed, collected a moment ago.
    public static Task<HttpResponseMessage> FileAsync(HttpClient client, Guid patient, Guid order, params (string Code, string Value)[] values) =>
        PostFormAsync(client, Path(patient, order), Path(patient, order),
            [.. values.Select(v => ($"Values[{v.Code}]", (object)v.Value)), ("CollectedAt", DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm"))]);

    // "Acknowledge results", as the page posts it: with what it showed.
    public static async Task<HttpResponseMessage> AcknowledgeAsync(HttpClient client, Guid patient, Guid order)
    {
        var page = await client.GetStringAsync(Path(patient, order), Cancel);
        return await client.PostAsync(Path(patient, order) + "?handler=Acknowledge", Form(("__RequestVerificationToken", Token(page)), ("Seen", SeenOn(page))), Cancel);
    }

    public static string SeenOn(string page) => System.Text.RegularExpressions.Regex.Match(page, "name=\"Seen\" value=\"([^\"]*)\"").Groups[1].Value;

    // "code value flag-or-normal" for each result of the order, by code.
    public static Task<List<string>> FiledAsync(WebApps apps, Guid patient) => ProxyTests.QueryAsync(apps,
        """
        SELECT code || ' ' || trim_scale(value) || ' ' || CASE WHEN value < reference_low THEN 'low' WHEN value > reference_high THEN 'high' ELSE 'normal' END
        FROM ehr.observations WHERE patient_id = @patient AND order_id IS NOT NULL ORDER BY code
        """, patient);

    public static async Task<string> StatusAsync(WebApps apps, Guid patient) =>
        string.Join(",", await ProxyTests.QueryAsync(apps, "SELECT status FROM ehr.orders WHERE patient_id = @patient ORDER BY id", patient));
}
