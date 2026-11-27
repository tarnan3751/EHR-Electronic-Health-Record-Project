using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Xunit;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-63
// Purpose / scenario: Who viewed my record names the part of the clinic's record each view was of (step 13c2): the
//   chart, the lists, orders, vital signs, booking an appointment, writing a message, who the record is shared with, a
//   note being written. Opening the chart is one line, not a line for each note it lists; views of a part whose page
//   records one read a view are counted in a minute.
// Type: Functional (successful flow: which part)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app. Dr. Test signed in, then put
//   the patient on their panel. Dr. Count is a clinician too.
// Inputs and system conditions: Dr. Test signs a note for the patient, then GETs the chart, the allergies' form, the lists'
//   history, the order form, the stopped orders, the vital signs, booking an appointment, a new message and the
//   proxies' page; the log holds, from Dr. Count, three views of the vital signs in one minute and a chart's opening
//   with the four notes it listed in another; the patient GETs /portal/views; on the on-site app.
// Test data: None beyond the accounts and the note.
// Steps:
//   1. Dr. Test writes and signs a note, then opens each page.
//   2. The patient opens Who viewed my record.
// Expected result: Dr. Test's lines name, each once or more and nothing else: "Draft visit note", "Chart", "Allergies,
//   medications and diagnoses", "Test and medicine orders", "Vital signs", "Appointments", "Messages", "Sharing"; no
//   line is "Visit note" or counts visit notes. Dr. Count's are "Vital signs, 3 times" and one "Chart" line.
// Creator: Tahseen Arnan
public class TC_UC6_63_EachPartOfTheChartNamed(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string Chart = "Chart";

    [Fact]
    public async Task Each_page_of_the_chart_is_named_and_the_chart_is_one_line()
    {
        var (_, session, patient) = await ViewedPatientAsync(apps, "Named");
        var count = await apps.CreateStaffAsync("Dr. Count", StaffRoles.Clinician);
        await PartReadsAsync(apps, patient.Patient, count.Id, 20,
            (AuditActions.Read, "patients", AuditParts.Vitals), (AuditActions.Read, "patients", AuditParts.Vitals), (AuditActions.Read, "patients", AuditParts.Vitals));
        await PartReadsAsync(apps, patient.Patient, count.Id, 30,
            [(AuditActions.Read, "patients", AuditParts.Chart), .. Enumerable.Repeat((AuditActions.Read, "clinical_notes", (string?)AuditParts.Chart), 4)]);

        // Step 1.
        await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        var chart = $"/patients/{patient.Patient}";
        foreach (var page in new[] { "", "/allergies", "/chart-history", "/orders/new", "/orders/discontinued", "/vitals", "/appointments/new", "/messages/new", "/proxies" })
        {
            using var response = await session.Client.GetAsync(chart + page, Cancel);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }

        // Step 2.
        var lines = Lines(await PageAsync(patient.Client));
        string[] Of(string who) => [.. lines.Where(line => line.StartsWith($"staff | {who}, clinician | ", StringComparison.Ordinal)).Select(line => line.Split(" | ")[2])];
        Assert.Equal(
            new[]
            {
                "Draft visit note", Chart, "Allergies, medications and diagnoses", "Test and medicine orders", "Vital signs",
                "Appointments", "Messages", "Sharing",
            }.Order(StringComparer.Ordinal),
            Of("Dr. Test").Select(Uncounted).Distinct().Order(StringComparer.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("| Visit note |", StringComparison.Ordinal) || line.Contains("visit notes |", StringComparison.Ordinal)
            && !line.Contains(Chart, StringComparison.Ordinal));
        Assert.Equal(["Vital signs, 3 times", Chart], Of("Dr. Count"));
    }
}
