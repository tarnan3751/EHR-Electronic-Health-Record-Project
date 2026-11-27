using System.Net;
using Ehr.Web.Ambient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;
using static Ehr.Web.Tests.AmbientFlows;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Listening to a visit ships switched off (Ehr:Ambient:Enabled, the expansion plan's step 16): the tests' apps switch it
// on, and these turn it off again. Off, nothing offers it, its page and every route of its API answer 404 and no consent
// can be written; a visit recorded while it was on is still opened, its transcript still deleted on time by the on-site
// timer, and its review still finished. The setting is true or false, or left out for false, and nothing else starts.
public class AmbientSwitchTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> SwitchedOff() =>
        apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting(AmbientListening.Setting, "false"));

    [Fact]
    public async Task Switched_off_listening_is_offered_nowhere_and_its_page_and_every_route_answer_404()
    {
        var sidecar = apps.Dictation.Reset();
        await using var off = SwitchedOff();
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Switched off");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(off, clinician);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("data-dictate-link", chart);
        Assert.DoesNotContain("data-listen-link", chart);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var editor = await created.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Dictate into this draft", editor);
        Assert.DoesNotContain("Listen to the visit", editor);
        using (var page = await session.Client.GetAsync($"/patients/{patient}/listen", Cancel))
        {
            // To a clinician whose patient this is, a plain page that says so, and the way back to the chart: never "no
            // patient, or nothing in their chart" (looking at the pages), and nothing about the patient read.
            Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(Cancel);
            Assert.Contains("Listening to a visit is switched off at this clinic.", html);
            Assert.Contains($"href=\"/patients/{patient}\"", html);
            Assert.DoesNotContain("No patient, or nothing in their chart, matches this address", html);
            Assert.DoesNotContain("Synthetic", html);
        }

        // To anyone else, the 404 a patient's address they can't see answers today, the same whether the patient exists
        // or not: the switched-off page tells no one of a patient they couldn't already find.
        var other = await SignInAsync(off, await apps.CreateStaffAsync("Dr. Elsewhere", Ehr.Data.Accounts.StaffRoles.Clinician));
        using (var theirs = await other.Client.GetAsync($"/patients/{patient}/listen", Cancel))
        using (var nobody = await other.Client.GetAsync($"/patients/{Guid.NewGuid()}/listen", Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
            var told = await theirs.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(await nobody.Content.ReadAsStringAsync(Cancel), told);
            Assert.DoesNotContain("switched off", told, StringComparison.Ordinal);
        }

        var token = Token(chart);
        var review = Guid.NewGuid();
        const string Id = "0123456789abcdef0123456789abcdef";
        using (var started = await Post(session.Client, token, $"{ListenApi}/start", Consent(review, patient)))
        {
            Assert.Equal(HttpStatusCode.NotFound, started.StatusCode);
        }

        foreach (var route in new[] { "audio?seq=0", "stop", "speakers", "result" })
        {
            using var answered = await Post(session.Client, token, $"{ListenApi}/{Id}/{route}");
            Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        }

        using (var deleted = await session.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{ListenApi}/{Id}")
        { Headers = { { "RequestVerificationToken", token } } }, Cancel))
        {
            Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        }

        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_consents WHERE patient_id = @id", patient));
        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ai_note_reviews WHERE patient_id = @id", patient));
        Assert.Empty(sidecar.Received);
    }

    // Recorded while listening was on, then switched off: the review opens with its transcript; once the transcript is
    // older than the clinic keeps it, the switched-off app's timer, which runs as it starts and then every hour, deletes
    // it; and the review is finished, every proposal rejected.
    [Fact]
    public async Task A_visit_recorded_while_it_was_on_is_opened_purged_and_finished_while_it_is_off()
    {
        apps.Dictation.Reset();
        var d = await DraftAsync(apps);
        await using (var off = SwitchedOff())
        {
            var session = await SignInAsync(off, await apps.ClinicianAsync());
            Assert.Contains("What brings you in today?", await session.Client.GetStringAsync(d.ReviewUrl, Cancel));
        }

        await using (var connection = await apps.OpenDatabaseAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Cancel))
        await using (var aged = new Npgsql.NpgsqlCommand(
            "SET LOCAL session_replication_role = replica; UPDATE ehr.ambient_transcripts SET created_at = created_at - interval '8 days' WHERE id = @id",
            connection, transaction))
        {
            aged.Parameters.AddWithValue("id", d.Listening.Review);
            await aged.ExecuteNonQueryAsync(Cancel);
            await transaction.CommitAsync(Cancel);
        }

        await using var later = SwitchedOff();
        var again = await SignInAsync(later, await apps.ClinicianAsync());
        for (var i = 0; i < 50 && (await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @id", d.Listening.Review))[0] != "0"; i++)
        {
            await Task.Delay(200, Cancel);
        }

        Assert.Equal(["0"], await RowsAsync(apps, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @id", d.Listening.Review));
        var html = await again.Client.GetStringAsync(d.ReviewUrl, Cancel);
        Assert.Contains("transcript was deleted after 7 days, so its proposals can", html);
        using var rejected = await ReviewPost(again.Client, Token(html), d.ReviewUrl, "RejectAll", ("Version", d.Version));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(["completed"], await RowsAsync(apps, "SELECT state FROM ehr.ai_note_reviews WHERE id = @id", d.Listening.Review));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("yes", null)]
    [InlineData("1", null)]
    [InlineData("", null)]
    public void The_switch_is_true_or_false_or_left_out_for_false(string? setting, bool? enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(AmbientListening.Setting, setting)]).Build();
        if (enabled is { } on)
        {
            Assert.Equal(new AmbientListening(on), AmbientListening.From(configuration));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => AmbientListening.From(configuration));
        }
    }
}
