using System.Text.Json;
using Ehr.Testing;
using Xunit;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-34
// Purpose / scenario: The desktop app's offline copy carries a note signed in four sections (the expansion plan's
//   step 14) with its Subjective and Objective, and gives them under History too, each under its heading, so a desktop
//   build that knows only History, Assessment and Plan shows all of the note; a note signed before step 14 comes as it
//   was signed.
// Type: Functional (alternative flow: the desktop app's offline copy)
// Requirements: proposed FR-49, proposed FR-60; UC 7's offline copy
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician has the patient on their panel, a note they signed in four sections, and a note signed
//   before step 14; they are signed in to the desktop app.
// Inputs and system conditions: POST /api/desktop/offline-panel with the desktop session.
// Test data: the four-section note's "Synthetic subjective: cough for two days." and "Synthetic objective: chest
//   clear."; the earlier note's History "Synthetic history: signed before four sections."
// Steps:
//   1. Download the offline copy.
// Expected result: The four-section note has subjective and objective, and history "Subjective\n…\n\nObjective\n…";
//   the earlier note has its History as signed and no subjective or objective.
// Creator: Tahseen Arnan
public class TC_UC14_34_OfflineCopyFourSections(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_offline_copy_carries_four_sections_and_history_for_older_builds()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        await SignedNoteAsync(session.Client, patient);
        await EarlierNotes.SignedAsync(apps.Database, clinician.Id, patient, "Synthetic history: signed before four sections.");

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        using var copy = JsonDocument.Parse(await offline.Content.ReadAsStringAsync(Cancel));
        var notes = copy.RootElement.EnumerateArray().Single(chart => chart.GetProperty("id").GetGuid() == patient).GetProperty("notes").EnumerateArray().ToList();
        Assert.Equal(2, notes.Count);
        var soap = notes.Single(note => note.GetProperty("subjective").ValueKind == JsonValueKind.String);
        Assert.Equal(Subjective, soap.GetProperty("subjective").GetString());
        Assert.Equal(Objective, soap.GetProperty("objective").GetString());
        Assert.Equal($"Subjective\n{Subjective}\n\nObjective\n{Objective}", soap.GetProperty("history").GetString());
        var earlier = notes.Single(note => note.GetProperty("subjective").ValueKind == JsonValueKind.Null);
        Assert.Equal("Synthetic history: signed before four sections.", earlier.GetProperty("history").GetString());
        Assert.Equal(JsonValueKind.Null, earlier.GetProperty("objective").ValueKind);
    }
}
