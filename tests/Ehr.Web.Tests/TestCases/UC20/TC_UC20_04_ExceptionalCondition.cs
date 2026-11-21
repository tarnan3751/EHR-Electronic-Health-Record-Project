using System.Net;
using Ehr.Testing;
using Ehr.Web.Api.Lab;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC20;

// Test Case ID: TC-UC20-04
// Purpose / scenario: Whatever isn't the lab, by its key, on the clinic's server, gets nothing through the door: a
//   request without the key or with a wrong one, a clinician's cookie or the desktop app's token with or without it,
//   and any request through the cloud site, which has no door and doesn't pass a lab's key on. A server with no lab's
//   key has no door: an address with nothing there is held to sign-in, as any is. A database with no key's hash (the
//   migration job run without EHR_LAB_KEY) lets no lab in, and the results pages, which go by the database, say
//   nothing of a lab.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-54, with NFR-06
// Use case: UC 20, Receive results from a lab
// Preconditions: A clinician on the on-site app has the patient on their panel and has signed a complete blood count
//   for them; they are signed in on both sites, and the desktop app has a session token.
// Inputs and system conditions: GET /api/lab/orders and POST /api/lab/orders/{id}/results on the on-site app with no
//   key, a wrong key, the clinician's cookie, the desktop token alone and with the lab's key; the same with the lab's
//   key on the cloud site, with its link to the on-site app up and cut off; on an on-site app with no Ehr:Lab:Key; and
//   with the database holding no key's hash. GET /patients/{id}/orders/{order}/results in each case.
// Test data: Hemoglobin (718-7) 9.1 g/dL; the wrong key "not-the-lab-key-but-long-enough-to-look-real".
// Steps:
//   1. Ask for orders and post results on the on-site app without a key, with a wrong key, with the clinician's cookie,
//      with the desktop token, and with the token beside the lab's key.
//   2. On the cloud site, with the lab's key: ask for orders; post results; post again with the link cut off.
//   3. On an on-site app without Ehr:Lab:Key: ask and post.
//   4. With the database holding no key's hash: ask, post, and open the results page; then with it back, open it again.
// Expected result: Step 1: 401 unauthorized each. Step 2: the ask finds no door (401, with no lab's answer, as for
//   any address before sign-in) and isn't forwarded; the post is forwarded without X-Lab-Key and refused 401
//   unauthorized; cut off, 503 "Not saved". Step 3: no door, each.
//   Step 4: 503 not-ready each, and the page has no "Lab connected"; with the hash back, it says "Lab connected:". No
//   result is filed and no order handed over.
// Creator: Tahseen Arnan
public class TC_UC20_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string WrongKey = "not-the-lab-key-but-long-enough-to-look-real";

    [Fact]
    public async Task Nothing_but_the_lab_on_the_clinics_server_gets_through_the_door()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var token = await apps.DesktopTokenAsync();
        var collected = DateTimeOffset.UtcNow.AddMinutes(-10);

        async Task RefusedAsync(HttpClient client, HttpStatusCode status, string error)
        {
            foreach (var answer in new[] { await client.GetAsync(Orders, Cancel), await PostAsync(client, order, collected, (Hemoglobin, 9.1m, "g/dL")) })
            {
                using (answer)
                {
                    Assert.Equal(status, answer.StatusCode);
                    Assert.Equal(error, await RefusalAsync(answer));
                }
            }
        }

        // An address with no endpoint, which the app holds to sign-in like any other: not the lab's answer.
        static async Task NoDoorAsync(HttpResponseMessage answer)
        {
            using (answer)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
                Assert.DoesNotContain("\"error\"", await answer.Content.ReadAsStringAsync(Cancel));
            }
        }

        // Step 1.
        await RefusedAsync(Client(apps.OnPrem, key: null), HttpStatusCode.Unauthorized, "unauthorized");
        await RefusedAsync(Client(apps.OnPrem, WrongKey), HttpStatusCode.Unauthorized, "unauthorized");
        await RefusedAsync(session.Client, HttpStatusCode.Unauthorized, "unauthorized");
        var desktop = Client(apps.OnPrem, key: null);
        desktop.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await RefusedAsync(desktop, HttpStatusCode.Unauthorized, "unauthorized");
        var desktopWithKey = Client(apps.OnPrem);
        desktopWithKey.DefaultRequestHeaders.Authorization = new("Bearer", token);
        await RefusedAsync(desktopWithKey, HttpStatusCode.Unauthorized, "unauthorized");

        // Step 2.
        await NoDoorAsync(await Client(apps.Cloud).GetAsync(Orders, Cancel));

        using (var posted = await PostAsync(Client(apps.Cloud), order, collected, (Hemoglobin, 9.1m, "g/dL")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, posted.StatusCode);
            Assert.Equal("unauthorized", await RefusalAsync(posted));
        }

        Assert.Contains($"POST {ResultsPath(order)}", apps.Forwarded);
        Assert.DoesNotContain(apps.Forwarded, request => request.StartsWith($"GET {Orders}", StringComparison.Ordinal));
        using (var cutOff = await PostAsync(Client(apps.CloudCutOff), order, collected, (Hemoglobin, 9.1m, "g/dL")))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, cutOff.StatusCode);
            Assert.Equal("Not saved: the on-prem server can't be reached.", await cutOff.Content.ReadAsStringAsync(Cancel));
        }

        // Step 3.
        await using (var noLab = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting(LabApiKey.Setting, "")))
        {
            await NoDoorAsync(await Client(noLab).GetAsync(Orders, Cancel));
            await NoDoorAsync(await PostAsync(Client(noLab), order, collected, (Hemoglobin, 9.1m, "g/dL")));
        }

        // Step 4: the migration job run without EHR_LAB_KEY stores no hash.
        Assert.Contains("data-lab-connected", await session.Client.GetStringAsync(Path(patient, order), Cancel));
        await MigratedDatabase.ExecuteAsync(apps.Database.OwnerConnectionString, "SELECT ehr.set_lab_key(NULL)");
        try
        {
            await RefusedAsync(Client(apps.OnPrem), HttpStatusCode.ServiceUnavailable, "not-ready");
            var page = await session.Client.GetStringAsync(Path(patient, order), Cancel);
            Assert.DoesNotContain("Lab connected", page);
            Assert.Contains("Enter results", page);
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(apps.Database.OwnerConnectionString, $"SELECT ehr.set_lab_key('{MigratedDatabase.LabKeyHash}')");
        }

        Assert.Contains("<span class=\"sc-notice__state\">Lab connected:</span> the lab files this order's results here when they're ready",
            await session.Client.GetStringAsync(Path(patient, order), Cancel));
        Assert.Equal(["0"], await ResultsOnFileAsync(apps, patient));
        Assert.Equal(0, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));
    }
}
