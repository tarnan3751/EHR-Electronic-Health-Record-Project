using System.Net;
using Ehr.Lab.Simulator;
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
// Purpose / scenario: Whatever isn't the lab, by its key alone, on the clinic's network, gets nothing through the door:
//   a request without the key or with a wrong one; the right key carried beside any of the app's sign-ins, a
//   clinician's cookie, a patient's portal cookie or the desktop app's token; and any request through the cloud site,
//   which has no door and forwards nothing under it. A server with no lab's key has no door: an address with nothing
//   there is held to sign-in, as any is. A database with no key's hash (the migration job run without EHR_LAB_KEY) lets
//   no lab in, and the results pages, which go by the database, say nothing of a lab. An order discontinued while the
//   lab works on it takes no results.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-54, with NFR-06
// Use case: UC 20, Receive results from a lab
// Preconditions: A clinician on the on-site app has the patient on their panel and has signed a complete blood count
//   for them; they are signed in on both sites, the desktop app has a session token, and the patient has a portal
//   account, signed in.
// Inputs and system conditions: GET /api/lab/orders and POST /api/lab/orders/{id}/results on the on-site app with no
//   key, a wrong key, and the lab's key beside the clinician's cookie, the portal cookie, the desktop token and a
//   cookie named as each sign-in's in other letter cases; the
//   same with the lab's key on the cloud site, with its link to the on-site app up and cut off; on an on-site app with
//   no Ehr:Lab:Key; and with the database holding no key's hash. GET /patients/{id}/orders/{order}/results in each
//   case. The simulated lab's code, in process.
// Test data: Hemoglobin (718-7) 9.1 g/dL; the wrong key "not-the-lab-key-but-long-enough-to-look-real".
// Steps:
//   1. Ask for orders and post results on the on-site app without a key, with a wrong key, with the clinician's cookie
//      and with the desktop token alone; then with the lab's key beside the clinician's cookie, beside the patient's
//      portal cookie, beside the desktop token, and beside a cookie named as each sign-in's in other letter cases
//      (__host-ehr.staff, __HOST-EHR.PATIENT, and a chunk of __Host-ehr.staffpending), with a value that isn't one.
//   2. On the cloud site, with the lab's key: ask for orders; post results; post again with the link cut off.
//   3. On an on-site app without Ehr:Lab:Key: ask and post.
//   4. With the database holding no key's hash: ask, post, and open the results page; then with it back, open it again.
//   5. Sign a TSH. Run a round of the simulated lab, which is handed it; discontinue the TSH; run a round once its
//      results are due.
// Expected result: Step 1: 401 unauthorized each. Step 2: no door, each time (401, with no lab's answer, as for any
//   address before sign-in), and nothing is forwarded to the clinic's server, link up or cut off. Step 3: no door,
//   each. Step 4: 503 not-ready each, and the page has no "Lab connected"; with the hash back, it says "Lab connected.".
//   Until then no result is filed and no order handed over. Step 5: the lab is handed the TSH, then told it was
//   discontinued and drops it; nothing is filed.
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
        // The right key, beside each way of being signed in to the app.
        var (portal, _) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var desktopWithKey = Client(apps.OnPrem);
        desktopWithKey.DefaultRequestHeaders.Authorization = new("Bearer", token);
        foreach (var signedIn in new[] { session.Client, portal, desktopWithKey })
        {
            if (!signedIn.DefaultRequestHeaders.Contains("X-Lab-Key"))
            {
                signedIn.DefaultRequestHeaders.Add("X-Lab-Key", MigratedDatabase.LabKey);
            }

            await RefusedAsync(signedIn, HttpStatusCode.Unauthorized, "unauthorized");
        }

        // A sign-in's cookie by its name in any case, and whatever its value.
        foreach (var cookie in new[] { "__host-ehr.staff=x", "__HOST-EHR.PATIENT=x", "__Host-ehr.staffpendingC1=x" })
        {
            var withCookie = Client(apps.OnPrem);
            withCookie.DefaultRequestHeaders.Add("Cookie", cookie);
            await RefusedAsync(withCookie, HttpStatusCode.Unauthorized, "unauthorized");
        }

        // Step 2.
        var forwarded = apps.Forwarded.Count;
        await NoDoorAsync(await Client(apps.Cloud).GetAsync(Orders, Cancel));
        await NoDoorAsync(await PostAsync(Client(apps.Cloud), order, collected, (Hemoglobin, 9.1m, "g/dL")));
        await NoDoorAsync(await PostAsync(Client(apps.CloudCutOff), order, collected, (Hemoglobin, 9.1m, "g/dL")));
        Assert.DoesNotContain(apps.Forwarded.Skip(forwarded), request => request.Contains(Orders, StringComparison.Ordinal));

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

        Assert.Contains("<span class=\"sc-notice__state\">Lab connected.</span> The lab files these results here when they're ready",
            await session.Client.GetStringAsync(Path(patient, order), Cancel));
        Assert.Equal(["0"], await ResultsOnFileAsync(apps, patient));
        Assert.Equal(0, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));

        // Step 5: discontinued while the lab works on it.
        var since = await WatermarkAsync(apps);
        var thyroid = await SignedAsync(session.Client, patient, "lab", "Thyroid-stimulating hormone (TSH)", Cbc);
        await SettledAsync(apps, patient);
        var log = new List<string>();
        var lab = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, log.Add, since);
        Assert.Equal(new LabRound(1, 0, 0, 1), await lab.RunOnceAsync((await apps.Database.NowAsync()).AddMinutes(-1), Cancel));
        using (await DiscontinueAsync(session.Client, patient, thyroid, "Synthetic: no longer needed"))
        {
        }

        Assert.Equal(new LabRound(0, 0, 1, 0), await lab.RunOnceAsync((await apps.Database.NowAsync()).AddMinutes(1), Cancel));
        Assert.Equal($"Order {thyroid}: the clinic takes no results for it from this lab (discontinued); dropped.", log[^1]);
        Assert.Equal(["0"], await ResultsOnFileAsync(apps, patient));
        Assert.Equal(1, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));
    }
}
