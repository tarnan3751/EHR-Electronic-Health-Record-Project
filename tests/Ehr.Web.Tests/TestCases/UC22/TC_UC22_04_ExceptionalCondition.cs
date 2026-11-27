using System.Net;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.PreVisitFormTests;

namespace Ehr.Web.Tests.TestCases.UC22;

// Test Case ID: TC-UC22-04
// Purpose / scenario: Off-site. A form is read from the cloud copy; sending it is forwarded to the clinic's server and
//   saved there. While the clinic's server can't be reached, the form isn't offered: the page says it can't be sent,
//   and still says where to get help now; a send that gets through anyway is refused, and nothing is saved.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-59, NFR-08
// Use case: UC 22, Answer a pre-visit form
// Preconditions: The clinician has the patient on their panel, an appointment two days ahead, and has asked for a
//   PHQ-9 and a GAD-7 before it. The patient is signed in to the portal.
// Inputs and system conditions: GET and POST /portal/forms/{form} on the cloud app with its link to the clinic up, and
//   on the cloud app cut off from it.
// Test data: Clinician "Dr. Forms Four". All "Not at all".
// Steps:
//   1. Off-site, with the link up, open the PHQ-9 and send it.
//   2. Off-site, cut off from the clinic, open the GAD-7, and send it anyway with a token from the clinic's site.
// Expected result: Step 1: the form is offered; the send is forwarded to the clinic's server and the answers are stored,
//   scored. Step 2: the page reads "Read-only. Forms can't be sent until the clinic's server is back.", has no form to
//   send, and still says "Not monitored." with 911 and 988; the send is answered 503 and nothing is stored.
// Creator: Tahseen Arnan
public class TC_UC22_04_ExceptionalCondition(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Off_site_a_form_is_sent_through_the_clinics_server_and_not_offered_while_it_cant_be_reached()
    {
        var visit = await VisitAsync(apps, "Dr. Forms Four");
        foreach (var code in new[] { Phq9, Gad7 })
        {
            using var asked = await AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, code);
        }

        var (_, jar) = await PortalAsync(apps, visit);
        var phq = await RequestAsync(apps, visit.Appointment, Phq9);
        var gad = await RequestAsync(apps, visit.Appointment, Gad7);

        var cloud = Client(apps.Cloud, jar);
        Assert.Contains("Send to my care team", await cloud.GetStringAsync(FormPath(phq), Cancel));
        var forwarded = apps.Forwarded.Count;
        using (var sent = await PostFormAsync(cloud, FormPath(phq), FormPath(phq),
            [.. Answers(PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0).Select(answer => ($"Answers[{answer.Key}]", (object)answer.Value))]))
        {
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        }

        Assert.True(apps.Forwarded.Count > forwarded, "The send was not forwarded to the clinic's server.");
        Assert.Equal(["0 Minimal"], await QueryAsync(apps, "SELECT score || ' ' || band FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", phq)));

        Assert.False(await apps.CloudCutOff.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
        var cutOff = Client(apps.CloudCutOff, jar);
        var pageHtml = await cutOff.GetStringAsync(FormPath(gad), Cancel);
        var page = Text(pageHtml);
        Assert.Contains("Read-only. Forms can't be sent until the clinic's server is back.", page);
        Assert.Contains("Not monitored. Your care team reads this form before your visit, not when you send it. If you need help now, call 911.", page);
        Assert.DoesNotContain("Send to my care team", page);
        Assert.DoesNotContain("<fieldset", pageHtml);
        using (var notSaved = await cutOff.PostAsync(FormPath(gad),
            Form([("__RequestVerificationToken", Token(await Client(apps.OnPrem, jar).GetStringAsync("/portal/access", Cancel))),
                .. Answers(GadItems, 0, 0, 0, 0, 0, 0, 0).Select(answer => ($"Answers[{answer.Key}]", (object)answer.Value))]), Cancel))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notSaved.StatusCode);
        }

        Assert.Empty(await QueryAsync(apps, "SELECT id::text FROM ehr.questionnaire_responses WHERE request_id = @request", ("request", gad)));
    }
}
