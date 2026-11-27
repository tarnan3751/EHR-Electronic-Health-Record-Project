using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-31
// Purpose / scenario: Off-site, the note editor is the cloud app's: its visit types and the vital signs it offers for
//   Objective are read from the replica, that read reaching the clinic's audit log through the cloud app's queue, and
//   saving is forwarded. While the clinic's server can't be reached, the draft is shown as last saved, read-only, and
//   neither a visit type nor the vital signs are offered.
// Type: Functional (exceptional condition)
// Requirements: FR-31, proposed FR-60; UC 2's off-site rules
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in, with the patient on their panel, a draft saved for a telephone visit, and
//   a heart rate recorded.
// Inputs and system conditions: GET /patients/{id}/notes/drafts/{draft} and its ?handler=Vitals on the cloud app with
//   the link to the clinic up, then on the cloud app cut off from the clinic.
// Test data: heart rate 72, taken a minute ago by the database's clock; Subjective "Synthetic subjective: by phone."
// Steps:
//   1. Off-site, open the draft and ask for the vital signs; let the cloud app ship its queue.
//   2. With the clinic's server unreachable, open the draft.
// Expected result: Step 1: the editor lists the visit types with the telephone visit chosen, and offers the vital
//   signs, whose text names the heart rate; the read reaches the clinic's audit log from the cloud, with the part
//   "vitals". Step 2: "It is shown as last saved.", the four sections as saved, and no visit type, vital signs or
//   editor.
// Creator: Tahseen Arnan
public class TC_UC14_31_FourSectionsOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_editor_reads_the_replica_and_with_the_server_down_shows_the_draft_as_saved()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        using (var recorded = await RecordAsync(apps, session.Client, patient, 1, (HeartRate, "72")))
        {
            Assert.Contains("Saved.", await ReadAsync(recorded));
        }

        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var (path, token) = (PathOf(created), Token(await ReadAsync(created)));
        using (var request = new HttpRequestMessage(HttpMethod.Post, path + "?handler=Save"))
        {
            request.Headers.Add("HX-Request", "true");
            request.Content = Form(("__RequestVerificationToken", token), ("Version", 1), ("NoteTemplateCode", "telephone"), ("Subjective", "Synthetic subjective: by phone."),
                ("Objective", ""), ("Assessment", ""), ("Plan", ""));
            using var saved = await session.Client.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        var cloud = Client(apps.Cloud, session.Jar);
        var editor = await cloud.GetStringAsync(path, Cancel);
        Assert.Matches("<option selected=\"selected\" value=\"telephone\"|<option value=\"telephone\"[^>]*selected=\"selected\"", editor);
        Assert.Contains("data-note-vitals", editor);
        var queue = apps.Cloud.Services.GetRequiredService<CloudAuditQueue>();
        var before = queue.ReadPending(int.MaxValue).Events.Count(e => e.PatientId == patient && e.Part == "vitals");
        var offer = await cloud.GetStringAsync(path + "?handler=Vitals", Cancel);
        Assert.Contains("heart rate 72 per minute", offer);
        Assert.Equal(before + 1, queue.ReadPending(int.MaxValue).Events.Count(e => e.PatientId == patient && e.Part == "vitals" && e.TableName == "patients"));
        await apps.ShipCloudAuditAsync();
        Assert.NotEmpty(await ProxyTests.QueryAsync(apps,
            $"SELECT id::text FROM ehr.audit_events WHERE patient_id = @patient AND actor_id = '{clinician.Id}' AND source = 'cloud' AND table_name = 'patients' AND part = 'vitals'", patient));

        var cutOff = await Client(apps.CloudCutOff, session.Jar).GetStringAsync(path, Cancel);
        Assert.Contains("It is shown as last saved.", cutOff);
        Assert.Matches("(?s)<h2>Subjective</h2><p class=\"sc-note__text\">Synthetic subjective: by phone\\.</p>\\s*<h2>Objective</h2>.*<h2>Assessment</h2>.*<h2>Plan</h2>", cutOff);
        Assert.DoesNotContain("data-note-template", cutOff);
        Assert.DoesNotContain("data-note-vitals", cutOff);
        Assert.DoesNotContain("<textarea", cutOff);
    }
}
