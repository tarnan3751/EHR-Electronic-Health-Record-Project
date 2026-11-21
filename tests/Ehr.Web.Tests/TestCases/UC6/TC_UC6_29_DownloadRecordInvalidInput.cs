using System.Net;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordDownload;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-29
// Purpose / scenario: The download takes no patient from the request. Whatever a request names, in its query, its path,
//   a form or a header, the copy is of the record the session is looking at; a proxy grant that isn't the account's
//   opens nothing. Someone signed out, and a clinician, are sent to the portal's sign-in.
// Type: Functional (invalid input)
// Requirements: proposed FR-56, NFR-06
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24 for patient A, signed in. Patient B, with an allergy of their own, has given access to a
//   third person. Dr. Elsewhere, a clinician, is signed in to the clinical app.
// Inputs and system conditions: GET /portal/record?handler=File and /portal/record/print with patientId, patient, id
//   and subject set to B's ID; GET /portal/record/{B's ID} and /portal/record/print/{B's ID}; POST /portal/record with
//   B's ID in the form; X-Patient-Id and X-Forwarded-For headers; the acting cookie set to the grant B gave the third
//   person; the same GETs signed out, and as the clinician.
// Test data: B's allergy "Synthetic: another patient's quince allergy".
// Steps:
//   1. As A, make each request.
//   2. Signed out, and as the clinician, ask for the file and the page.
// Expected result: Step 1: every file and page is A's (A's MRN in it) and has nothing of B's; the paths with an ID are
//   404; the POST gets no file and nothing of anyone's record; with the cookie naming another account's grant, A is sent to their own record, told the
//   access ended. Nothing is recorded against B. Step 2: sent to /portal/sign-in, nothing recorded.
// Creator: Tahseen Arnan
public class TC_UC6_29_DownloadRecordInvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_request_can_name_whose_record_is_copied()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Invalid");
        var other = await ProxyTests.PersonAsync(apps, "Synthetic", "Otherone");
        await apps.InsertAllergyAsync(other.Patient, "Synthetic: another patient's quince allergy");
        var (_, othersGrant) = await VisitSummaryTests.ProxyForAsync(apps, other, "Otherproxy", sensitive: true);
        var b = other.Patient.ToString();
        var otherMrn = WebApps.MrnOf(other.Patient);

        void IsOwn(string copy)
        {
            Assert.Contains(chart.Mrn, copy);
            Assert.DoesNotContain(otherMrn, copy);
            Assert.DoesNotContain("quince", copy);
            Assert.DoesNotContain("Otherone", copy);
        }

        foreach (var name in new[] { "patientId", "patient", "id", "subject" })
        {
            var file = await DownloadAsync(patient.Client, path: $"{FilePath}&{name}={b}");
            Assert.Equal(HttpStatusCode.OK, file.Status);
            IsOwn(file.Body);
            IsOwn(await patient.Client.GetStringAsync($"{PrintPath}?{name}={b}", Cancel));
        }

        foreach (var path in new[] { $"{PagePath}/{b}", $"{PrintPath}/{b}", $"{PagePath}/{b}?handler=File" })
        {
            using var notFound = await patient.Client.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        }

        // A form post: the page has no handler for one, so there is no copy, of anyone.
        using (var posted = await PostFormAsync(patient.Client, PagePath + "?handler=File", PagePath, ("patientId", b), ("sensitive", "true")))
        {
            Assert.NotEqual(FhirRequests.ContentType, posted.Content.Headers.ContentType?.ToString());
            var body = await posted.Content.ReadAsStringAsync(Cancel);
            Assert.DoesNotContain("quince", body);
            Assert.DoesNotContain(chart.Mrn, body);
        }

        foreach (var (header, value) in new[] { ("X-Patient-Id", b), ("X-Forwarded-For", "10.0.0.7"), ("Patient", b) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, FilePath);
            request.Headers.TryAddWithoutValidation(header, value);
            using var response = await patient.Client.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            IsOwn(await response.Content.ReadAsStringAsync(Cancel));
        }

        // A grant that is someone else's: forgotten before anything is read, and A is back on their own record.
        patient.Jar.Add(Base, new Cookie(Ehr.Web.ActingCookie.Name, othersGrant.ToString()) { Secure = true, Path = "/" });
        using (var forged = await patient.Client.GetAsync(FilePath, Cancel))
        {
            Assert.Equal("/portal", PathOf(forged));
            var home = await ReadAsync(forged);
            Assert.Contains("Access ended:", Words(home));
            Assert.DoesNotContain("quince", home);
        }

        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Download, patientId: other.Patient));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, patient.Account, other.Patient));

        // Signed out, and as a clinician: sent to the portal's sign-in.
        var clinician = await Browser.SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Elsewhere", Ehr.Data.Accounts.StaffRoles.Clinician));
        foreach (var client in new[] { Client(apps.OnPrem, new CookieContainer()), clinician.Client })
        {
            foreach (var path in new[] { FilePath, PrintPath, PagePath })
            {
                using var refused = await client.GetAsync(path, Cancel);
                Assert.Equal("/portal/sign-in", PathOf(refused));
            }
        }

        // A's own: one download for each file above that answered.
        Assert.Equal(7, await apps.AuditEventsAsync(AuditActions.Download, patient.Account, patient.Patient));
    }
}
