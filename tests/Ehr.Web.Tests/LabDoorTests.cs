using System.Globalization;
using System.Net;
using Ehr.Lab.Simulator;
using Ehr.Testing;
using Ehr.Web.Api.Lab;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.LabTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests;

// The lab's door (the expansion plan's step 12) beyond its test cases (TestCases/UC20), from the review of the first
// series: the database refuses a wrong key by itself, given the hash of the key the caller presented; a failure behind
// the door is its fixed answer, never the developer exception page, in Development too; and times are instants, whatever
// offset a lab sends and whatever time zone the database's session has.
public class LabDoorTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    const string WrongKey = "not-the-lab-key-but-long-enough-to-look-real";

    // The app's own check of the key, taken away, as a mutation: it holds the right key, and lets any key through. Given
    // the hash of its own key, as the first series did, the database would let a wrong key in.
    sealed record AnyKey() : LabApiKey(MigratedDatabase.LabKey)
    {
        public override bool Admits(byte[] presented) => true;
    }

    [Fact]
    public async Task The_database_refuses_a_wrong_key_by_itself_when_the_apps_check_lets_it_through()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var since = await WatermarkAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        await SettledAsync(apps, patient);
        await using var mutated = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<LabApiKey>(new AnyKey())));

        foreach (var key in new[] { WrongKey, null })
        {
            using var asked = await Client(mutated, key).GetAsync($"{Orders}?since={since}", Cancel);
            Assert.Equal("not-ready", await RefusalAsync(asked));
            using var posted = await PostAsync(Client(mutated, key), order, DateTimeOffset.UtcNow.AddMinutes(-5), (Hemoglobin, 9.1m, "g/dL"));
            Assert.Equal("not-ready", await RefusalAsync(posted));
        }

        // The right key still gets in through the mutated app: the database was what refused the others.
        using (var right = await Client(mutated).GetAsync($"{Orders}?since={since}", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }

        Assert.Equal(["0"], await ResultsOnFileAsync(apps, patient));
        Assert.Equal(1, await apps.AuditEventsAsync("sent_to_lab", patientId: patient));
    }

    // In Development (as WebApps runs the on-prem app), a failure behind the door is still the door's fixed answer, and
    // the log names what failed truthfully: a refusal by the database that isn't its refusal of the key is not taken for
    // a key that differs.
    [Fact]
    public async Task A_failure_is_the_doors_fixed_answer_even_in_development_and_is_logged_as_what_it_is()
    {
        var logs = new LoggingTests.CapturedLogs();
        await using var app = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(logs)));
        await MigratedDatabase.ExecuteAsync(apps.Database.OwnerConnectionString, "REVOKE EXECUTE ON FUNCTION ehr.lab_orders(text, xid8) FROM ehr_app");
        try
        {
            using var failed = await Client(app).GetAsync(Orders, Cancel);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal("application/json", failed.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", failed.Headers.CacheControl?.ToString());
            var body = await failed.Content.ReadAsStringAsync(Cancel);
            Assert.DoesNotContain(MigratedDatabase.LabKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain("X-Lab-Key", body, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("failed", await RefusalAsync(failed));
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(apps.Database.OwnerConnectionString, "GRANT EXECUTE ON FUNCTION ehr.lab_orders(text, xid8) TO ehr_app");
        }

        var lines = logs.Messages.ToList();
        Assert.Contains("Error Ehr.Web.Api.Lab.LabApi: A request to the lab's door failed: PostgresException, SQLSTATE 42501.", lines);
        Assert.DoesNotContain(lines, line => line.Contains("migration job", StringComparison.Ordinal) || line.Contains(MigratedDatabase.LabKey, StringComparison.Ordinal));
    }

    // A collection time is an instant: sent with any offset, it is filed as the same one.
    [Fact]
    public async Task A_collection_time_with_any_offset_is_the_instant_it_names()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        var collected = DateTimeOffset.UtcNow.AddMinutes(-20);
        collected = collected.AddTicks(-(collected.Ticks % TimeSpan.TicksPerMillisecond));
        var chicago = collected.ToOffset(TimeSpan.FromHours(-6)).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        Assert.EndsWith("-06:00", chicago, StringComparison.Ordinal);

        using var filed = await PostRawAsync(Client(apps.OnPrem), order, $$"""{"collectedAt":"{{chicago}}","results":[{"code":"718-7","value":9.1,"unit":"g/dL"}]}""");

        Assert.Equal(HttpStatusCode.NoContent, filed.StatusCode);
        var stored = (await ProxyTests.QueryAsync(apps,
            "SELECT to_char(effective_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ehr.observations WHERE patient_id = @patient AND order_id IS NOT NULL", patient)).Single();
        Assert.Equal(collected, DateTimeOffset.Parse(stored, CultureInfo.InvariantCulture), TimeSpan.FromMicroseconds(1));
    }

    // The whole loop with the app's database sessions in another time zone, as a server's role may be set: the hand-over
    // still says when an order was signed in UTC, and the simulated lab still files.
    [Fact]
    public async Task The_loop_works_when_the_apps_database_sessions_are_in_another_time_zone()
    {
        await MigratedDatabase.ExecuteAsync(apps.Database.SuperuserConnectionString, "ALTER ROLE ehr_app SET timezone = 'America/Chicago'");
        NpgsqlConnection.ClearAllPools();
        try
        {
            var (_, patient, session) = await OnPanelAsync(apps);
            await apps.InsertConditionAsync(patient, "Hypothyroidism, unspecified", code: "E03.9");
            var since = await WatermarkAsync(apps);
            var order = await SignedAsync(session.Client, patient, "lab", "Thyroid-stimulating hormone (TSH)", Cbc);
            await SettledAsync(apps, patient);

            var (asked, body) = await HandOverAsync(Client(apps.OnPrem), since);
            Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
            Assert.EndsWith("Z", Assert.Single(body.GetProperty("orders").EnumerateArray()).GetProperty("signedAt").GetString(), StringComparison.Ordinal);

            var lab = new LabSimulator(Client(apps.OnPrem), LabSettings.Default, _ => { }, since);
            Assert.Equal(new LabRound(1, 0, 0, 1), await lab.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(-1), Cancel));
            Assert.Equal(new LabRound(0, 1, 0, 0), await lab.RunOnceAsync(DateTimeOffset.UtcNow.AddMinutes(1), Cancel));
            Assert.Contains(await FiledAsync(apps, patient), row => row.StartsWith("3016-3 ", StringComparison.Ordinal) && row.EndsWith(" high", StringComparison.Ordinal));
            Assert.Equal("completed", await StatusAsync(apps, patient));
            Assert.Equal("America/Chicago", await SessionTimeZoneAsync());
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(apps.Database.SuperuserConnectionString, "ALTER ROLE ehr_app RESET timezone");
            NpgsqlConnection.ClearAllPools();
        }
    }

    // The time zone the app's role's sessions start in, as a fresh session of it reports it.
    async Task<string> SessionTimeZoneAsync()
    {
        await using var connection = new NpgsqlConnection(apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SHOW timezone", connection);
        return (string)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
