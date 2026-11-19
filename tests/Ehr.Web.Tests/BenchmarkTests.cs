using System.Net;
using Ehr.Bench;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Ehr.Web.Tests;

// The benchmark's app (bench/Ehr.Bench) against a database whose key ring it didn't make and can't read: never a new
// Data Protection key there, which would sit unencrypted in a key ring shared by both of the stack's apps. And --stack
// refuses before it starts without the certificate. Its own database's commits wait for the disk, and it can tell a
// database whose commits don't. --assert, on made-up times, gates each kind of request's p99 of each request less its
// own commit, and nothing else; a full time's p99 not under the budget only gets a warning.
public class BenchmarkTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Its_gate_passes_a_page_over_20_ms_only_with_its_commits()
    {
        var (code, output) = Gate(Row("Slow commits", (1_000, Total: 30, Audit: 5, Commit: 15)));

        Assert.Equal(0, code);
        Assert.Contains("--assert passed", output);
    }

    // The slowest requests aren't the ones with the slowest commits: the whole request's p99 less the commits' p99,
    // 30 - 25, would pass.
    [Fact]
    public void Its_gate_takes_each_requests_own_commit_out_and_names_the_page_it_fails()
    {
        var (code, output) = Gate(
            Row("Fast", (1_000, Total: 8, Audit: 1, Commit: 1)),
            Row("Slow renders", (20, Total: 30, Audit: 2, Commit: 0), (20, Total: 30, Audit: 2, Commit: 25), (960, Total: 5, Audit: 1, Commit: 1)));

        Assert.Equal(1, code);
        Assert.Contains($"--assert failed: Slow renders: p99 excluding the commit {30.0:F1} ms, not under 20 ms.", output);
        Assert.DoesNotContain("Fast", output);
    }

    [Fact]
    public void Its_gate_names_every_page_it_fails()
    {
        var (code, output) = Gate(
            Row("First slow", (1_000, Total: 30, Audit: 2, Commit: 5)),
            Row("Fast", (1_000, Total: 8, Audit: 1, Commit: 1)),
            Row("Second slow", (1_000, Total: 40, Audit: 2, Commit: 5)));

        Assert.Equal(1, code);
        Assert.Contains($"--assert failed: First slow: p99 excluding the commit {25.0:F1} ms, not under 20 ms.", output);
        Assert.Contains($"--assert failed: Second slow: p99 excluding the commit {35.0:F1} ms, not under 20 ms.", output);
        Assert.DoesNotContain("Fast", output);
    }

    // Without the audit insert too it would be 8 ms, and pass.
    [Fact]
    public void Its_gate_keeps_the_audit_insert_in()
    {
        var (code, output) = Gate(Row("Slow audit", (1_000, Total: 25, Audit: 15, Commit: 2)));

        Assert.Equal(1, code);
        Assert.Contains($"--assert failed: Slow audit: p99 excluding the commit {23.0:F1} ms", output);
    }

    [Theory]
    [InlineData(19.5, 0)]
    [InlineData(20.0, 1)]
    public void Its_gate_needs_a_p99_under_20_ms(double excludingCommit, int expected)
    {
        var (code, _) = Gate(Row("Boundary", (1_000, Total: excludingCommit + 5, Audit: 1, Commit: 5)));

        Assert.Equal(expected, code);
    }

    [Fact]
    public void It_warns_of_a_page_over_20_ms_only_with_its_commits_and_still_passes()
    {
        var row = Row("Slow commits", (1_000, Total: 30, Audit: 5, Commit: 15));

        var (code, output) = Finish(assert: true, row);

        Assert.Equal(0, code);
        Assert.Contains($"warning: Slow commits: p99 including the commit {30.0:F1} ms, not under 20 ms. This doesn't fail the run. "
            + "On the clinic server, this is the target to meet; on CI runners, shared disks make commit times unsteady.", output);
        Assert.Contains("--assert passed", output);
        Assert.Equal("  warning: full time not under 20 ms", RenderBenchmark.Markers(row));
    }

    // Over both ways, so that the gate, had it run, would have failed.
    [Fact]
    public void Without_assert_it_still_warns_and_exits_0()
    {
        var (code, output) = Finish(assert: false, Row("Slow both", (1_000, Total: 40, Audit: 2, Commit: 10)));

        Assert.Equal(0, code);
        Assert.Contains($"warning: Slow both: p99 including the commit {40.0:F1} ms, not under 20 ms.", output);
        Assert.DoesNotContain("--assert", output);
    }

    [Fact]
    public void It_warns_of_nothing_when_every_full_time_is_under_20_ms()
    {
        var row = Row("Fast", (1_000, Total: 8, Audit: 1, Commit: 1));

        var (code, output) = Finish(assert: false, row);

        Assert.Equal(0, code);
        Assert.Equal("", output);
        Assert.Equal("", RenderBenchmark.Markers(row));
    }

    [Theory]
    [InlineData(19.5, false)]
    [InlineData(20.0, true)]
    public void It_warns_when_the_full_times_p99_is_20_ms_or_more(double total, bool expected)
    {
        var row = Row("Boundary", (1_000, Total: total, Audit: 1, Commit: 5));

        var (code, output) = Finish(assert: true, row);

        Assert.Equal(0, code);
        Assert.Equal(expected, row.FullTimeOverBudget);
        Assert.Equal(expected, output.Contains("warning: Boundary:", StringComparison.Ordinal));
        Assert.Equal(expected, RenderBenchmark.Markers(row).Contains("warning: full time not under 20 ms", StringComparison.Ordinal));
    }

    [Fact]
    public void A_page_over_20_ms_either_way_fails_the_gate_and_gets_the_warning()
    {
        var row = Row("Slow both", (1_000, Total: 40, Audit: 2, Commit: 10));

        var (code, output) = Finish(assert: true, row);

        Assert.Equal(1, code);
        Assert.Contains($"warning: Slow both: p99 including the commit {40.0:F1} ms, not under 20 ms.", output);
        Assert.Contains($"--assert failed: Slow both: p99 excluding the commit {30.0:F1} ms, not under 20 ms.", output);
        Assert.Equal("  not under 20 ms excluding the commit  warning: full time not under 20 ms", RenderBenchmark.Markers(row));
    }

    static (int Code, string Output) Gate(params RenderBenchmark.Result[] results)
    {
        using var output = new StringWriter();
        var code = RenderBenchmark.Gate(results, output);
        return (code, output.ToString());
    }

    // After the table, as a run does, with or without --assert: what it prints and its exit code.
    static (int Code, string Output) Finish(bool assert, params RenderBenchmark.Result[] results)
    {
        using var output = new StringWriter();
        var code = RenderBenchmark.Finish(results, assert, output);
        return (code, output.ToString());
    }

    // A kind of request's 1,000 measured requests, in groups that take the same times.
    static RenderBenchmark.Result Row(string name, params (int Count, double Total, double Audit, double Commit)[] groups)
    {
        var requests = groups.SelectMany(group => Enumerable.Repeat(group, group.Count)).ToList();
        return new RenderBenchmark.Result(
            name, [.. requests.Select(r => r.Total)], [.. requests.Select(r => r.Audit)], [.. requests.Select(r => r.Commit)]);
    }

    // UC 2 (Tahseen Arnan): off-site, the page waits for the disk in its audit queue's append, not in the database, so
    // that counts as the commit, and the gate leaves it out as it does an on-prem page's commit.
    [Fact]
    public void Off_site_the_audit_queues_append_counts_as_the_commit()
    {
        var times = new RenderBenchmark.DatabaseTimes();
        var queue = new RenderBenchmark.TimedAuditQueue(new SlowQueue(), times);

        queue.Enqueue([]);

        Assert.True(times.CommitMilliseconds >= SlowQueue.Milliseconds, $"{times.CommitMilliseconds} ms");
        Assert.Equal(0, times.AuditMilliseconds);
    }

    sealed class SlowQueue : Ehr.Web.CloudAudit.IDurableAuditQueue
    {
        public const int Milliseconds = 30;

        public void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents) => Thread.Sleep(Milliseconds);
    }

    [Fact]
    public async Task Its_own_database_waits_for_the_disk_at_every_commit()
    {
        await using var database = MigratedDatabase.Durable();
        await database.InitializeAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);

        var durability = await RenderBenchmark.ReadDurabilityAsync(connection);

        Assert.Equal(new RenderBenchmark.Durability(Fsync: "on", SynchronousCommit: "on", FullPageWrites: "on"), durability);
        Assert.True(durability.IsOn);
    }

    // The tests' own database, as Testcontainers starts PostgreSQL: the benchmark would mark its table and refuse
    // --assert.
    [Fact]
    public async Task It_tells_a_database_whose_commits_dont_wait_for_the_disk()
    {
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);

        var durability = await RenderBenchmark.ReadDurabilityAsync(connection);

        Assert.Equal(new RenderBenchmark.Durability(Fsync: "off", SynchronousCommit: "off", FullPageWrites: "off"), durability);
        Assert.False(durability.IsOn);
    }

    [Fact]
    public void Against_the_stack_without_the_certificate_it_refuses_before_starting()
    {
        var without = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ehr"] = "Host=127.0.0.1;Database=ehr",
        }).Build();
        var with = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ehr"] = "Host=127.0.0.1;Database=ehr",
            ["Ehr:DataProtection:Certificate"] = "certs/example.com.pem",
        }).Build();

        Assert.Contains("Ehr:DataProtection:Certificate", RenderBenchmark.StackRefusal(without));
        Assert.Null(RenderBenchmark.StackRefusal(with));
    }

    // The web tests' on-prem app made this database's key, encrypted with a certificate the benchmark's app hasn't got:
    // the case in which an app writes a new, unencrypted key as it starts.
    [Fact]
    public async Task Its_app_adds_no_key_to_a_key_ring_it_cannot_read()
    {
        using (var onPrem = Browser.CreateClient(apps.OnPrem))
        {
            Assert.Equal(HttpStatusCode.OK, (await onPrem.GetAsync("/sign-in", Cancel)).StatusCode);
        }

        var before = await KeysAsync();

        await using (var app = new RenderBenchmark.BenchApp(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword)))
        {
            using var client = app.CreateClient(new() { BaseAddress = Browser.Base });
            using var signIn = await client.GetAsync("/sign-in", Cancel);
            Assert.Contains("__RequestVerificationToken", await Browser.ReadAsync(signIn));
        }

        Assert.NotEmpty(before);
        Assert.Equal(before, await KeysAsync());
    }

    async Task<List<string>> KeysAsync()
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT friendly_name FROM ehr.data_protection_keys ORDER BY id", connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var keys = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
