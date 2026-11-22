using System.Net;
using Xunit;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-03
// Purpose / scenario: A lab's post the clinic can't take is refused whole, with a fixed word and sentence that carry
//   nothing about a patient, and files nothing: a body that isn't the contract's, and results for an order that isn't
//   open to them, a test it doesn't report, a unit that isn't the test's, a collection time ahead of the server's clock
//   or a value that differs from one already filed. A hand-over asked for with anything but a watermark is refused too.
// Type: Functional (invalid input)
// Requirements: proposed FR-54
// Use case: UC 20, Receive results from a lab
// Preconditions: A lab is connected: the on-site app and the database hold its key. A clinician on the on-site app
//   has the patient on their panel and has signed, through the order form, a complete blood count (open), a TSH
//   (discontinued), a vitamin D (one result filed by hand and acknowledged) and a chest X-ray.
// Inputs and system conditions: POST /api/lab/orders/{id}/results and GET /api/lab/orders with the lab's key in
//   X-Lab-Key, on the on-site app.
// Test data: Hemoglobin (718-7) 9.1 g/dL; the bodies and query strings below; a random order ID.
// Steps:
//   1. POST bodies that aren't the contract's: not JSON, JSON with an unknown field, without collectedAt, with no
//      results, a test twice, a negative value, a value with five decimals, 51 results, and one over 16 KB.
//   2. POST hemoglobin for an unknown order, the X-ray, the discontinued TSH and the acknowledged vitamin D; then to
//      the count a test it doesn't report, hemoglobin in g/L, and hemoglobin collected tomorrow.
//   3. POST hemoglobin 9.1 to the count, then 9.2.
//   4. GET the hand-over with since not a number, twice, and beside another parameter.
//   5. POST each of the shapes JSON readers commonly let through: a null result, null fields, a number in a string, a
//      field twice, a top-level array or null, an empty body, a time that isn't one, a time with neither a Z nor an
//      offset (which would be read in the server's own zone), a date alone, a number too large to read.
// Expected result: Step 1: 415 unsupported for the text, 413 too-large for the large body, 400 malformed for the rest.
//   Step 2: 404 unknown-order for the unknown order and the X-ray; 409 discontinued; 409 closed; 422 not-reported;
//   422 wrong-unit; 422 in-the-future. Step 3: 204, then 409 different-result, and 9.1 stays. Steps 4 and 5: 400
//   malformed, each.
//   Every refusal is JSON with exactly an error word and a message, holds neither the patient's name, MRN or ID nor the
//   order's ID, and is no-store. Nothing but the one hemoglobin is filed, and nothing is handed over.
// Creator: Tahseen Arnan
public class TC_UC20_03_InvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_post_the_clinic_cant_take_is_refused_whole_in_fixed_words_and_files_nothing()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var count = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var thyroid = await SignedAsync(session.Client, patient, "lab", "Thyroid-stimulating hormone (TSH)", Cbc);
        using (await DiscontinueAsync(session.Client, patient, thyroid, "Synthetic: ordered twice"))
        {
        }

        var vitaminD = await SignedAsync(session.Client, patient, "lab", "Vitamin D, 25-hydroxy", Cbc);
        using (await FileAsync(apps, session.Client, patient, vitaminD, ("62292-8", "41")))
        using (await AcknowledgeAsync(session.Client, patient, vitaminD))
        {
        }

        var xray = await SignedAsync(session.Client, patient, "imaging", "Chest X-ray, 2 views", [("indication", "Synthetic cough")]);
        Assert.Equal("active,revoked,completed,active", await StatusAsync(apps, patient));
        Assert.Equal(["1"], await ProxyTests.QueryAsync(apps, "SELECT count(*)::text FROM ehr.result_acknowledgments WHERE patient_id = @patient", patient));
        var lab = Client(apps.OnPrem);
        var collected = DateTimeOffset.UtcNow.AddMinutes(-10);
        var mrn = WebApps.MrnOf(patient);

        async Task RefusedAsync(HttpResponseMessage response, HttpStatusCode status, string error, Guid? order = null)
        {
            using (response)
            {
                Assert.Equal(status, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                var text = await response.Content.ReadAsStringAsync(Cancel);
                foreach (var secret in new[] { patient.ToString(), mrn, "Orderable", (order ?? count).ToString() })
                {
                    Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
                }

                Assert.Equal(error, await RefusalAsync(response));
            }
        }

        // Step 1.
        var hemoglobin = """{"code":"718-7","value":9.1,"unit":"g/dL"}""";
        var tooMany = string.Join(",", Enumerable.Range(0, 51).Select(i => $"{{\"code\":\"c{i}\",\"value\":1,\"unit\":\"u\"}}"));
        await RefusedAsync(await PostRawAsync(lab, count, "collected 9.1", "text/plain"), HttpStatusCode.UnsupportedMediaType, "unsupported");
        foreach (var body in new[]
        {
            "not json",
            $$"""{"collectedAt":"{{collected:O}}","results":[{{hemoglobin}}],"patient":"x"}""",
            $$"""{"results":[{{hemoglobin}}]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[{{hemoglobin}},{{hemoglobin}}]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[{"code":"718-7","value":-1,"unit":"g/dL"}]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[{"code":"718-7","value":9.12345,"unit":"g/dL"}]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[{"code":"718-7","value":9.1}]}""",
            $$"""{"collectedAt":"{{collected:O}}","results":[{{tooMany}}]}""",
        })
        {
            await RefusedAsync(await PostRawAsync(lab, count, body), HttpStatusCode.BadRequest, "malformed");
        }

        await RefusedAsync(await PostRawAsync(lab, count, $$"""{"collectedAt":"{{collected:O}}","results":[{{hemoglobin}}],"padding":"{{new string('x', 17 * 1024)}}"}"""),
            HttpStatusCode.RequestEntityTooLarge, "too-large");

        // Step 2.
        var unknown = Guid.CreateVersion7();
        await RefusedAsync(await PostAsync(lab, unknown, collected, (Hemoglobin, 9.1m, "g/dL")), HttpStatusCode.NotFound, "unknown-order", unknown);
        await RefusedAsync(await PostAsync(lab, xray, collected, (Hemoglobin, 9.1m, "g/dL")), HttpStatusCode.NotFound, "unknown-order", xray);
        await RefusedAsync(await PostAsync(lab, thyroid, collected, ("3016-3", 2.1m, "m[IU]/L")), HttpStatusCode.Conflict, "discontinued", thyroid);
        await RefusedAsync(await PostAsync(lab, vitaminD, collected, ("62292-8", 42m, "ng/mL")), HttpStatusCode.Conflict, "closed", vitaminD);
        await RefusedAsync(await PostAsync(lab, count, collected, (Hemoglobin, 9.1m, "g/dL"), ("4548-4", 6.1m, "%")), HttpStatusCode.UnprocessableEntity, "not-reported");
        await RefusedAsync(await PostAsync(lab, count, collected, (Hemoglobin, 91m, "g/L")), HttpStatusCode.UnprocessableEntity, "wrong-unit");
        await RefusedAsync(await PostAsync(lab, count, DateTimeOffset.UtcNow.AddDays(1), (Hemoglobin, 9.1m, "g/dL")), HttpStatusCode.UnprocessableEntity, "in-the-future");

        // Step 3.
        using (var filed = await PostAsync(lab, count, collected, (Hemoglobin, 9.1m, "g/dL")))
        {
            Assert.Equal(HttpStatusCode.NoContent, filed.StatusCode);
        }

        await RefusedAsync(await PostAsync(lab, count, collected, (Hemoglobin, 9.2m, "g/dL")), HttpStatusCode.Conflict, "different-result");
        Assert.Equal(["62292-8 41 normal", $"{Hemoglobin} 9.1 low"], await FiledAsync(apps, patient));

        // Step 4.
        var handedBefore = await apps.AuditEventsAsync("sent_to_lab", patientId: patient);
        foreach (var query in new[] { "?since=abc", "?since=1&since=2", "?since=1&patient=x", "?since=-1", "?since=99999999999999999999999" })
        {
            await RefusedAsync(await lab.GetAsync(Orders + query, Cancel), HttpStatusCode.BadRequest, "malformed");
        }

        Assert.Equal(handedBefore, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));
    }

    // Step 5, one shape at a time, against a fresh order: each is malformed, and files nothing.
    public static TheoryData<string> Shapes =>
    [
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[null]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"unit":"g/dL"},null]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":null,"value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"unit":null}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":null,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":"9.1","unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"value":9.2,"unit":"g/dL"}]}""",
        """[{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}]""",
        """null""",
        "",
        """{"collectedAt":null,"results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":null}""",
        """{"collectedAt":"yesterday","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":1e400,"unit":"g/dL"}]}""",
        """{"collectedAt":"2026-11-20T15:00:00Z","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}],"results2":[]}""",
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task A_shape_the_contract_does_not_name_is_malformed(string body)
    {
        using var refused = await PostRawAsync(Client(apps.OnPrem), Guid.CreateVersion7(), body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("malformed", await RefusalAsync(refused));
    }
}
