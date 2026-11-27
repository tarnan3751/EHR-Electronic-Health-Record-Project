using System.Net;
using Xunit;
using static Ehr.Web.Tests.ChartListTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-09
// Purpose / scenario: An entry with a name that isn't one of the standard codes, no name, a date that doesn't exist
//   or is in the future, a status that isn't one of the list's, or, for an allergy, a severity or risk that isn't one
//   of the form's or a reaction too long, is refused: the field is highlighted with what to fix, nothing is saved,
//   and what was entered stays.
// Type: Functional (invalid input)
// Requirements: FR-02, proposed FR-38
// Use case: UC 4, View a patient's chart (editing the chart's lists)
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel, with nothing in the
//   three lists. The clinic's server is up.
// Inputs and system conditions: POST /patients/{id}/diagnoses, /patients/{id}/medications and
//   /patients/{id}/allergies on the on-site app.
// Test data: Names "", "Synthetic made-up disease" and "Essential (primary) hypertension". Onset "2024-02-30" and
//   "2999-01-01". Status "deleted". A medication stopped (2024-01-01) before it started (2024-06-01). An allergy to
//   "Peanut" with severity "extreme", with risk "certain", and with a reaction of 201 characters.
// Steps:
//   1. Save a diagnosis with no name; then with the made-up name.
//   2. Save the real diagnosis with each bad onset date; then with the status "deleted".
//   3. Save a medication whose stop date is before its start date.
//   4. Save the allergy with each bad severity, risk and reaction.
// Expected result: Each is answered 422 with "Not saved." and, in turn: "Enter the diagnosis."; "Choose a diagnosis
//   from the list."; "Enter a date that exists and isn't in the future, as year-month-day." (twice); "Choose a
//   status."; "The stop date is before the start date."; "Choose a severity."; "Choose a risk."; "Shorten this to
//   200 characters or fewer." The name typed is still in its field, marked invalid where it is the problem, and the
//   field at fault is marked invalid. No row exists in any of the three lists.
// Creator: Tahseen Arnan (expansion step 2), UC 4 owner Diego Valenzuela
public class TC_UC4_09_EntryInvalidInput(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_entry_that_is_not_a_known_code_or_has_a_bad_date_is_refused_and_nothing_is_saved()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        const string real = "Essential (primary) hypertension";

        foreach (var (fields, message) in new ((string, object)[] Fields, string Message)[]
        {
            ([("Name", ""), ("Status", "active")], "Enter the diagnosis."),
            ([("Name", "Synthetic made-up disease"), ("Status", "active")], "Choose a diagnosis from the list."),
            ([("Name", real), ("Status", "active"), ("Onset", "2024-02-30")], "Enter a date that exists and isn&#x27;t in the future, as year-month-day."),
            ([("Name", real), ("Status", "active"), ("Onset", "2999-01-01")], "Enter a date that exists and isn&#x27;t in the future, as year-month-day."),
            ([("Name", real), ("Status", "deleted")], "Choose a status."),
        })
        {
            using var refused = await SaveAsync(session.Client, patient, "diagnoses", null, fields);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var html = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains(message, html);
            Assert.Contains("Not saved.", html);
            if (fields[0].Item2 is string { Length: > 0 } typed)
            {
                Assert.Contains($"name=\"Name\" value=\"{typed}\"", html);
            }
        }

        using var invalidName = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Synthetic made-up disease"), ("Status", "active"));
        Assert.Matches("<input [^>]*name=\"Name\"[^>]*aria-invalid=\"true\" aria-describedby=\"entry-name-error entry-name-hint\"",
            await invalidName.Content.ReadAsStringAsync(Cancel));

        using var dates = await SaveAsync(session.Client, patient, "medications", null,
            ("Name", "lisinopril 10 MG Oral Tablet"), ("Status", "stopped"), ("Started", "2024-06-01"), ("Stopped", "2024-01-01"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dates.StatusCode);
        Assert.Contains("The stop date is before the start date.", await dates.Content.ReadAsStringAsync(Cancel));

        foreach (var (field, value, id, message) in new[]
        {
            ("Severity", "extreme", "entry-severity", "Choose a severity."),
            ("Criticality", "certain", "entry-criticality", "Choose a risk."),
            ("Reaction", new string('x', 201), "entry-reaction", "Shorten this to 200 characters or fewer."),
        })
        {
            using var refused = await SaveAsync(session.Client, patient, "allergies", null, ("Name", "Peanut"), ("Status", "active"), (field, value));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var html = await refused.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Not saved.", html);
            Assert.Contains(message, html);
            Assert.Matches($"<(?:input|select) id=\"{id}\"[^>]*aria-invalid=\"true\" aria-describedby=\"{id}-error\"", html);
            Assert.Contains("name=\"Name\" value=\"Peanut\"", html);
        }

        Assert.Empty(await RowsAsync(apps, "conditions", patient, "display"));
        Assert.Empty(await RowsAsync(apps, "medication_statements", patient, "display"));
        Assert.Empty(await RowsAsync(apps, "allergy_intolerances", patient, "display"));
    }
}
