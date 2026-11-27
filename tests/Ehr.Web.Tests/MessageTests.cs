using Ehr.Data.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Secure messages (FR-18; the expansion plan's step 5): helpers for the test cases in TestCases/UC6 (the patient's
// side) and TestCases/UC17 (the clinician's). What the database refuses whatever the app does is in
// Ehr.Security.Tests (MessageSecurityTests).
static class MessageTests
{
    // A clinician with a patient on their panel, both signed in: the clinician on the on-site app, the patient in the
    // portal.
    public static async Task<(StaffAccount Clinician, Guid Patient, Session Staff, (HttpClient Client, System.Net.CookieContainer Jar) Portal)> CareAsync(
        WebApps apps, string name)
    {
        var clinician = await apps.CreateStaffAsync(name, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Messaged");
        await apps.AssignAsync(clinician, patient);
        return (clinician, patient, await SignInAsync(apps.OnPrem, clinician), await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient));
    }

    // The patient starts a conversation in the portal; the new thread's ID, from where the browser is sent.
    public static async Task<Guid> StartAsync(HttpClient portal, string subject, string body)
    {
        using var sent = await PostFormAsync(portal, "/portal/messages/new", "/portal/messages/new", ("Subject", subject), ("Body", body));
        return Guid.Parse(sent.RequestMessage!.RequestUri!.AbsolutePath.Split('/').Last());
    }

    public static Task<List<string>> RowsAsync(WebApps apps, Guid patient, string expression, string table = "messages", string order = "sent_at, id", string where = "true") =>
        apps.PatientRowsAsync($"SELECT {expression} FROM ehr.{table} WHERE patient_id = @patient AND {where} ORDER BY {order}", patient);

    public static Task<List<string>> OpenInboxAsync(WebApps apps, Guid patient) =>
        RowsAsync(apps, patient, "recipient_id::text", "inbox_items", "created_at", "done_at IS NULL");
}
