using System.Text.RegularExpressions;
using Ehr.Data.Patients;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.RecordDownload;
using static Ehr.Web.Tests.VisitSummaryTests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-26
// Purpose / scenario: The copy holds what the portal lets the patient see and nothing more, even with sensitive items
//   asked for: never a signed note, an addendum or a draft, never a withdrawn summary's text, never a value entered in
//   error, never a result not yet released or not final, never an appointment's cancellation reason, and nothing from
//   the audit log. The file holds no kind of resource but those it is for. The printable page's visit summaries and
//   placeholders are exactly what Visit summaries shows, so the two can't drift apart.
// Type: Functional (alternative flow: what the copy leaves out)
// Requirements: proposed FR-56, FR-16, proposed FR-50, proposed FR-55
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: As TC-UC6-24. Dr. Nothingmore, with the patient on their panel, has signed a note and an addendum to
//   it, and has a draft for the patient; has released a summary and withdrawn it, and released a summary of a second
//   note; has marked the blood pressure reading
//   entered in error. The record also holds a medication entered in error, a result not yet released and a preliminary
//   one.
// Inputs and system conditions: GET /portal/record?handler=File&sensitive=true and /portal/record/print?sensitive=true,
//   on the on-site app.
// Test data: The note's history "Synthetic history: dry cough, quartzwing pattern, nocturnal."; the addendum "Synthetic
//   addendum: zebrafinch follow-up."; the draft "Synthetic draft: kingfisher"; the withdrawn summary "We talked about the
//   wrong visit."; the reading's reason "Synthetic: wrong cuff size"; "Synthetic retracted medication"; "Synthetic
//   unreleased test"; "Synthetic preliminary test"; the cancellation reason "Synthetic: the patient asked to move it".
// Steps:
//   1. Download the file, sensitive items included.
//   2. Open the printable page, sensitive items included.
// Expected result: Neither has any of the test data's words, nor the blood pressure reading in error. The file holds only
//   Patient, AllergyIntolerance, MedicationStatement, Condition, Observation and Appointment resources, and its results
//   are the two released ones. The page has the standing summary, then the withdrawn summary's placeholder: the same
//   items, in the same order and with the same words, as /portal/summaries?sensitive=true renders.
// Creator: Tahseen Arnan
public class TC_UC6_26_RecordHoldsNothingMore(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_copy_never_holds_notes_withdrawn_or_retracted_items_or_unreleased_results()
    {
        var (chart, patient) = await PortalChartAsync(apps, "Dr. Nothingmore");
        var client = chart.Session.Client;
        var note = await SignedNoteAsync(client, chart.Patient, history: NoteHistory);
        using (var added = await AddAsync(client, chart.Patient, note, "Synthetic addendum: zebrafinch follow-up."))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, added.StatusCode);
        }

        await FhirRequests.InsertAsync(apps,
            """
            INSERT INTO ehr.clinical_drafts (id, patient_id, clinician_id, body, version, history, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @clinician, 'Synthetic draft: kingfisher', 1, 'Synthetic draft: kingfisher', 'Synthetic draft: kingfisher', 'Synthetic draft: kingfisher', 1, false)
            """, chart.Patient, ("clinician", chart.Clinician.Id));
        using (var released = await ReleaseAsync(client, chart.Patient, note, "We talked about the wrong visit."))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        using (var withdrawn = await WithdrawAsync(client, chart.Patient, note, await StandingAsync(apps, chart.Patient)))
        {
            Assert.Contains("Withdrawn:", Words(await ReadAsync(withdrawn)));
        }

        // And a summary that stands, of a second note.
        using (var released = await ReleaseAsync(client, chart.Patient, await SignedNoteAsync(client, chart.Patient)))
        {
            Assert.Contains("Released:", Words(await ReadAsync(released)));
        }

        using (await VitalTests.MarkAsync(apps, client, chart.Patient, chart.VitalsAt, "Synthetic: wrong cuff size", VitalSign.Systolic))
        {
            Assert.Contains("8462-4 82 entered-in-error", await VitalTests.StatesAsync(apps, chart.Patient));
        }

        await apps.InsertMedicationAsync(chart.Patient, "Synthetic retracted medication", MedicationStatementStatus.EnteredInError);
        var now = DateTimeOffset.UtcNow;
        await apps.InsertResultAsync(chart.Patient, "Synthetic unreleased test", "2345-7", 99m, "mg/dL", 70m, 99m, now.AddDays(-1), issuedAt: null);
        await apps.InsertResultAsync(chart.Patient, "Synthetic preliminary test", "2345-7", 98m, "mg/dL", 70m, 99m, now.AddDays(-1), now, status: "preliminary");

        var file = await DownloadAsync(patient.Client, sensitive: true);
        var bundle = Bundle(file);
        var page = await PrintAsync(patient.Client, sensitive: true);
        foreach (var copy in new[] { file.Body, Words(page) })
        {
            foreach (var never in new[]
            {
                "quartzwing", NoteAssessment, NotePlan, "zebrafinch", "kingfisher", "wrong visit", "wrong cuff size", "Synthetic retracted medication",
                "Synthetic unreleased test", "Synthetic preliminary test", "asked to move it", "audit",
            })
            {
                Assert.DoesNotContain(never, copy, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.All(bundle.Entry, entry => Assert.Contains(entry.Resource?.TypeName,
            new[] { "Patient", "AllergyIntolerance", "MedicationStatement", "Condition", "Observation", "Appointment" }));
        var observations = Entries<R4.Observation>(bundle);
        Assert.Equal(["Hemoglobin A1c", "Thyroid stimulating hormone"], observations
            .Where(o => o.Category.Any(c => c.Coding.Any(coding => coding.Code == "laboratory"))).Select(o => o.Code?.Text).Order());
        // The reading in error is gone, its two values with it; the other vital signs stand.
        Assert.DoesNotContain(observations, o => o.Code?.Coding.Any(c => c.Code is "85354-9" or "8480-6" or "8462-4") == true);
        Assert.Contains(observations, o => o.Code?.Coding.Any(c => c.Code == VitalSign.HeartRate) == true);
        Assert.DoesNotContain("128/82", Words(page));
        Assert.Contains("was withdrawn by your care team.", Words(page));

        // The printable page's summaries and placeholders are the summaries page's: the same items, in the same order,
        // with the same words, so the two can't drift apart.
        var printed = SummaryItems(page);
        Assert.Equal(["standing", "withdrawn"], printed.Select(item => item.Kind));
        Assert.Equal(SummaryItems(await PortalAsync(patient.Client, sensitive: true)), printed);
    }

    // Each visit summary or placeholder on a page, in order: its kind and its words, white space run together.
    static List<(string Kind, string Words)> SummaryItems(string html) =>
        [.. Regex.Matches(html, "<li\\b[^>]*\\bdata-summary=\"(?<kind>[a-z]+)\"[^>]*>(?<body>.*?)</li>", RegexOptions.Singleline)
            .Select(item => (item.Groups["kind"].Value, Regex.Replace(Words(item.Groups["body"].Value), "\\s+", " ").Trim()))];
}
