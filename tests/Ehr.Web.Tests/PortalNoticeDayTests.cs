using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.RefillRequestTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests;

// The portal's "something new" email (PortalNotices) goes at most once a day for released visit summaries (step 13a)
// and for answers to refill requests (step 13d), and a day is the clinic's calendar day, from midnight in its time zone
// (UTC in these tests). The review of step 13d found that both counted the last 24 hours instead, so the first event of
// a day that came less than 24 hours after the one before told the patient nothing. Here three events on three days in
// a row, each less than 24 hours after the one before, are three emails, and a second the same day none: the earlier
// events are moved back to just before midnight by the superuser, past the triggers that would refuse it.
public class PortalNoticeDayTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Two days back at 23:59, and yesterday at 23:58: 23 hours 59 minutes apart, and the second less than a day before
    // anything done today, unless it is done in today's last two minutes.
    static (DateTimeOffset TwoDaysAgo, DateTimeOffset Yesterday) LateOnTheDaysBefore()
    {
        var midnight = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        return (midnight.AddDays(-1).AddMinutes(-1), midnight.AddMinutes(-2));
    }

    [Fact]
    public async Task Answers_to_refill_requests_on_three_days_in_a_row_are_three_emails_and_two_on_one_day_are_one()
    {
        var (_, session, patient) = await PortalPatientAsync(apps, "Daily");
        await MedicationAsync(apps, patient.Patient);
        var (twoDaysAgo, yesterday) = LateOnTheDaysBefore();
        var before = EmailsTo(apps, patient.Email).Count;

        async Task<Guid> AnswerAsync()
        {
            using (var asked = await AskAsync(patient.Client, Lisinopril))
            {
                Assert.Equal(PortalPath, PathOf(asked));
            }

            var request = await RequestIdAsync(apps, patient.Patient);
            using var declined = await DeclineAsync(session.Client, patient.Patient, request);
            Assert.Contains("Declined.", Said(await ReadAsync(declined)));
            return request;
        }

        await MoveAsync("UPDATE ehr.refill_requests SET requested_at = requested_at - (decided_at - @at), decided_at = @at WHERE id = @id", await AnswerAsync(), twoDaysAgo);
        await MoveAsync("UPDATE ehr.refill_requests SET requested_at = requested_at - (decided_at - @at), decided_at = @at WHERE id = @id", await AnswerAsync(), yesterday);
        await AnswerAsync();
        Assert.Equal(3, EmailsTo(apps, patient.Email).Count - before);

        await AnswerAsync();
        Assert.Equal(3, EmailsTo(apps, patient.Email).Count - before);
    }

    [Fact]
    public async Task Visit_summaries_released_on_three_days_in_a_row_are_three_emails_and_two_on_one_day_are_one()
    {
        var (_, patient, session) = await OrderTests.OnPanelAsync(apps);
        var (email, _) = await apps.CreatePatientAccountAsync(patient);
        var (twoDaysAgo, yesterday) = LateOnTheDaysBefore();

        async Task<Guid> ReleasedAsync()
        {
            var note = await SignedNoteAsync(session.Client, patient);
            using (var released = await ReleaseAsync(session.Client, patient, note, "We talked about your follow-up."))
            {
                Assert.Contains("Released.", Words(await ReadAsync(released)));
            }

            return await StandingAsync(apps, patient);
        }

        await MoveAsync("UPDATE ehr.visit_summaries SET released_at = @at WHERE id = @id", await ReleasedAsync(), twoDaysAgo);
        await MoveAsync("UPDATE ehr.visit_summaries SET released_at = @at WHERE id = @id", await ReleasedAsync(), yesterday);
        await ReleasedAsync();
        Assert.Equal(3, EmailsTo(apps, email).Count);

        await ReleasedAsync();
        Assert.Equal(3, EmailsTo(apps, email).Count);
    }

    // Moves an event back in time as the superuser, with the triggers that refuse such a change off for the statement.
    async Task MoveAsync(string sql, Guid id, DateTimeOffset at)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var transaction = await db.BeginTransactionAsync(Cancel);
        await using (var off = new NpgsqlCommand("SET LOCAL session_replication_role = replica", db, transaction))
        {
            await off.ExecuteNonQueryAsync(Cancel);
        }

        await using (var command = new NpgsqlCommand(sql, db, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("at", at);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(Cancel));
        }

        await transaction.CommitAsync(Cancel);
    }
}
