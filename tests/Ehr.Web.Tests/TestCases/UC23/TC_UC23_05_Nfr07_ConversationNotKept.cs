using Ehr.Web.Ambient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;

namespace Ehr.Web.Tests.TestCases.UC23;

// Test Case ID: TC-UC23-05
// Purpose / scenario: A visit's conversation is kept no longer than it is needed, and never where it shouldn't be: not in
//   the review's own record, not in the audit log, not in the app's logs, and not at the dictation service once staged.
// Type: Non-functional (privacy)
// Requirements: NFR-07, proposed FR-62
// Use case: UC 23, Draft a note from a recorded visit
// Preconditions: The clinician has the patient on their panel; the on-site app, with the stub dictation service; the
//   app's logs captured.
// Inputs and system conditions: The whole visit, from consent to the review finished; the on-site app's setting for how
//   long a transcript of a review left open is kept (Ehr:Ambient:KeepTranscriptDays).
// Test data: The stub's conversation, whose words are known.
// Steps:
//   1. Listen to a visit, draft it and stage its review.
//   2. Finish the review.
//   3. Set how long a transcript is kept: left out, 1, 7, 0, a half, 2.5 and 8 days.
// Expected result: Step 1: the review's stored result has no transcript text; the transcript is kept apart, and the audit
//   log holds only its hash and length; the service was told to discard the session. Step 2: no transcript kept; no log
//   line from the visit holds any word said in it. Step 3: 7 days when left out, and whole days from 1 to 7 as set;
//   anything else stops the app from starting. (The database's rules for all three: AmbientSecurityTests.)
// Creator: Tahseen Arnan
public class TC_UC23_05_Nfr07_ConversationNotKept(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_conversation_is_kept_apart_only_while_reviewed_and_never_in_the_record_the_audit_log_or_a_log()
    {
        using var logs = new LoggingTests.CapturedLogs();
        apps.OnPrem.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        var sidecar = apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        var l = d.Listening;

        Assert.Equal([""], await RowsAsync(apps, "SELECT result_json #>> '{transcript,text}' FROM ehr.ai_note_reviews WHERE id = @id", l.Review));
        Assert.Equal(["1"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @id", l.Review));
        Assert.Equal(["0"], await RowsAsync(apps,
            "SELECT count(*) FROM ehr.audit_events WHERE patient_id = @id AND details::text LIKE '%What brings you in today%'", l.Patient));
        Assert.Equal("DELETE", sidecar.Received.Last().Method);

        var proposals = await ProposalsAsync(apps, l.Review);
        using var rejected = await ReviewPost(l.Session.Client, l.Token, d.ReviewUrl, "RejectAll", ("Version", d.Version));
        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @id", l.Review));
        Assert.NotEmpty(proposals);
        Assert.DoesNotContain(logs.Messages, message => message.Contains("brings you in", StringComparison.Ordinal)
            || message.Contains("cough", StringComparison.OrdinalIgnoreCase) || message.Contains("chest", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("0", null)]
    [InlineData("0.5", null)]
    [InlineData("2.5", null)]
    [InlineData("8", null)]
    public void A_transcript_of_a_review_left_open_is_kept_whole_days_from_1_to_7(string? setting, int? days)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(AmbientTranscriptPurgeService.Setting, setting)]).Build();
        if (days is { } kept)
        {
            Assert.Equal(TimeSpan.FromDays(kept), AmbientTranscriptPurgeService.KeepFrom(configuration));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => AmbientTranscriptPurgeService.KeepFrom(configuration));
        }
    }
}
