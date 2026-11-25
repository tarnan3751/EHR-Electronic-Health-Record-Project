// Rehearses a point-in-time restore from the local server stack's backups (this folder), and fails unless it works.
// For the local stack and CI's stack job, with synthetic data only. With the stack up:
//
//   dotnet run restore-drill.cs
//
// It runs `docker compose` in this folder as you would, so COMPOSE_PROJECT_NAME and COMPOSE_ENV_FILES pick a stack other
// than the default one. What it does, and so proves:
//   1. Waits until `barman check onprem` passes: a base backup exists and WAL is streaming. Then, saying so first,
//      removes any restore-db container and anything at /var/lib/barman/restore left from before: a restore by hand in
//      progress there would be lost, so don't run the drill during one.
//   2. On the primary, as the superuser over its local socket, writes a "before" row into a database of its own,
//      restore_drill (never into ehr), then notes the time T and, at that moment, a count and a checksum of a few of
//      ehr's tables. A moment later it writes an "after" row: the restore must stop between the two, and recovery needs
//      a commit after T to know it has reached T.
//   3. Ships the WAL still in progress to Barman, and picks the newest base backup that ended before T. A restore only
//      goes forward from a backup, so a newer one is no use; if none ended before T, it says so and fails.
//   4. Restores that backup and the WAL after it to T, inside the Barman volume (/var/lib/barman/restore), and starts
//      the restore-db service on it, which replays to T and pauses there, read-only. Nothing touches the primary's data.
//   5. Checks that the restored copy started and took connections, holds "before" and not "after", replayed nothing
//      after T, and has the same counts and checksums as the primary had at T.
//   6. Cleans up whatever happened: removes restore-db with the empty volume its image declares, deletes
//      /var/lib/barman/restore, and drops restore_drill on the primary. The checksums are taken from one snapshot at T, so a write to those tables in that instant makes the
//      drill fail, naming the table: run it on a stack nobody is using.
// infra/compose/README.md ("Backup and point-in-time restore") has the steps by hand, for a restore that is needed.

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

var drill = new Drill(Paths.Here());
return await drill.RunAsync();

static class Paths
{
    // This folder, which `docker compose` runs in.
    public static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

sealed class Drill(string folder)
{
    const string Database = "restore_drill";
    const string RestorePath = "/var/lib/barman/restore";

    // Tables whose count and checksum must come back as they were at T: the migrations, the largest reference table,
    // and two that patient data fills on a seeded stack.
    static readonly string[] Tables = ["__ef_migrations_history", "clinical_codes", "patients", "audit_events"];

    readonly Stopwatch clock = Stopwatch.StartNew();
    int failures;

    public async Task<int> RunAsync()
    {
        Say("Restore drill: point-in-time restore from Barman's backups into restore-db.");
        try
        {
            await WaitForBackupsAsync();
            Say($"Removing any restore-db container and anything at {RestorePath} first (a restore by hand in progress there would be lost)");
            await CleanUpAsync(quiet: true);

            await PrimaryAsync("postgres", $"CREATE DATABASE {Database}");
            await PrimaryAsync(Database, "CREATE TABLE marker (name text PRIMARY KEY, written_at timestamptz NOT NULL DEFAULT clock_timestamp()); INSERT INTO marker (name) VALUES ('before');");

            // One snapshot for T and the checksums: T is read first, so the snapshot is T's.
            var lines = await PrimaryAsync("ehr",
                "BEGIN ISOLATION LEVEL REPEATABLE READ;\n" +
                "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.US') || '+00:00';\n" +
                ChecksumSql() +
                "COMMIT;\n");
            var target = lines[0];
            var atTarget = lines.Skip(1).ToList();
            Say($"Target time T: {target}");

            await Task.Delay(TimeSpan.FromSeconds(2));
            await PrimaryAsync(Database, "INSERT INTO marker (name) VALUES ('after');");

            await BarmanAsync("switch-wal", "--force", "--archive", "onprem");
            var backup = await BackupBeforeAsync(target);
            Say($"Restoring base backup {backup} and the WAL after it to T");
            await BarmanAsync("recover", "onprem", backup, RestorePath, "--target-time", target, "--target-action", "pause");

            await ComposeAsync(["--profile", "restore", "up", "--detach", "restore-db"]);
            await WaitUntilPausedAsync();

            Check("The restored copy started and takes connections", true);
            var markers = string.Join(",", await RestoredAsync(Database, "SELECT name FROM marker ORDER BY name"));
            Check($"It holds the row written before T and not the one written after (found: {markers})", markers == "before");
            var replayed = (await RestoredAsync("postgres", $"SELECT pg_last_xact_replay_timestamp() <= '{target}'::timestamptz")).Single();
            Check("It replayed nothing committed after T", replayed == "t");
            var restored = await RestoredAsync("ehr", ChecksumSql());
            foreach (var (table, (was, now)) in Tables.Zip(atTarget.Zip(restored)))
            {
                Check($"ehr.{table}: {Rows(now)} rows, the count and checksum the primary had at T", was == now);
            }
        }
        catch (DrillException e)
        {
            Check(e.Message, false);
        }
        finally
        {
            await CleanUpAsync(quiet: false);
        }

        Say(failures == 0
            ? $"Restore drill passed in {clock.Elapsed.TotalSeconds:F0} s."
            : $"Restore drill FAILED ({failures} check(s)) after {clock.Elapsed.TotalSeconds:F0} s.");
        return failures == 0 ? 0 : 1;
    }

    static string ChecksumSql() => string.Concat(Tables.Select(table =>
        $"SELECT count(*) || ' ' || md5(coalesce(string_agg(t::text, E'\\n' ORDER BY t::text), '')) FROM ehr.{table} t;\n"));

    static string Rows(string countAndChecksum) => countAndChecksum.Split(' ')[0];

    async Task WaitForBackupsAsync()
    {
        for (var tries = 0; tries < 120; tries++)
        {
            if ((await ComposeAsync(["exec", "-T", "-u", "postgres", "barman", "barman", "check", "onprem"], check: false)).Exit == 0)
            {
                Say("barman check onprem passes");
                return;
            }

            if (tries == 0)
            {
                Say("Waiting for barman check onprem to pass (Barman's first base backup takes a few minutes)");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        throw new DrillException("barman check onprem didn't pass within 10 minutes: there is no backup to restore from");
    }

    // The newest base backup that finished before the target, by Barman's own record of each.
    async Task<string> BackupBeforeAsync(string target)
    {
        var when = DateTimeOffset.Parse(target);
        var listed = JsonDocument.Parse(string.Join("\n", await BarmanAsync("-f", "json", "list-backups", "onprem")));
        var candidates = new List<(string Id, DateTimeOffset End)>();
        foreach (var backup in listed.RootElement.GetProperty("onprem").EnumerateArray())
        {
            if (backup.GetProperty("status").GetString() != "DONE")
            {
                continue;
            }

            var id = backup.GetProperty("backup_id").GetString()!;
            var shown = JsonDocument.Parse(string.Join("\n", await BarmanAsync("-f", "json", "show-backup", "onprem", id)));
            var end = shown.RootElement.GetProperty("onprem").GetProperty("base_backup_information").GetProperty("end_time").GetString()!;
            candidates.Add((id, DateTimeOffset.Parse(end)));
        }

        var usable = candidates.Where(c => c.End < when).OrderByDescending(c => c.End).ToList();
        if (usable.Count == 0)
        {
            throw new DrillException(candidates.Count == 0
                ? "No base backup has finished, so nothing can be restored"
                : "No base backup finished before T: a restore only goes forward from a backup, so T can't be reached");
        }

        if (usable[0].End != candidates.Max(c => c.End))
        {
            Say($"The newest base backup ended after T; using {usable[0].Id}, the newest that ended before it");
        }

        return usable[0].Id;
    }

    async Task WaitUntilPausedAsync()
    {
        for (var tries = 0; tries < 120; tries++)
        {
            var state = await ComposeAsync(["exec", "-T", "-u", "postgres", "restore-db",
                "psql", "-qAtX", "-c", "SELECT pg_is_in_recovery() || ' ' || pg_get_wal_replay_pause_state()"], check: false);
            if (state.Exit == 0 && state.Out.Trim() == "true paused")
            {
                Say("restore-db replayed to T and paused there, read-only");
                return;
            }

            if (state.Exit == 0 && state.Out.Trim().StartsWith("false", StringComparison.Ordinal))
            {
                throw new DrillException("restore-db finished recovery instead of pausing at T");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        var logs = await ComposeAsync(["--profile", "restore", "logs", "--tail", "30", "restore-db"], check: false);
        Console.Error.WriteLine(logs.Out + logs.Err);
        throw new DrillException("restore-db didn't reach T and pause within 2 minutes (its log is above)");
    }

    async Task CleanUpAsync(bool quiet)
    {
        var steps = new[]
        {
            // --volumes: the anonymous volume the postgres image declares, which would otherwise be left behind.
            await ComposeAsync(["--profile", "restore", "rm", "--stop", "--force", "--volumes", "restore-db"], check: false),
            await ComposeAsync(["exec", "-T", "-u", "postgres", "barman", "rm", "-rf", RestorePath], check: false),
            await ComposeAsync(["exec", "-T", "-u", "postgres", "onprem-db",
                "psql", "-qAtX", "-v", "ON_ERROR_STOP=1", "-d", "postgres", "-c", $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)"], check: false),
        };
        if (!quiet)
        {
            Check("Cleaned up: restore-db removed, the restored copy deleted, restore_drill dropped", steps.All(s => s.Exit == 0));
        }
    }

    Task<List<string>> PrimaryAsync(string database, string sql) => PsqlAsync("onprem-db", database, sql);

    Task<List<string>> RestoredAsync(string database, string sql) => PsqlAsync("restore-db", database, sql);

    async Task<List<string>> PsqlAsync(string service, string database, string sql)
    {
        var result = await ComposeAsync(
            ["exec", "-T", "-u", "postgres", service, "psql", "-qAtX", "-v", "ON_ERROR_STOP=1", "-d", database, "-f", "-"], input: sql);
        return Lines(result.Out);
    }

    async Task<List<string>> BarmanAsync(params string[] arguments) =>
        Lines((await ComposeAsync(["exec", "-T", "-u", "postgres", "barman", "barman", .. arguments])).Out);

    // `docker compose` in this folder, with the caller's environment. A failure that matters stops the drill with what
    // the command said.
    async Task<(int Exit, string Out, string Err)> ComposeAsync(string[] arguments, bool check = true, string? input = null)
    {
        var start = new ProcessStartInfo("docker")
        {
            WorkingDirectory = folder,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("compose");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new DrillException("docker could not be started");
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var result = (process.ExitCode, await output, await error);
        if (check && result.ExitCode != 0)
        {
            throw new DrillException($"docker compose {string.Join(' ', arguments.Take(6))} failed ({result.ExitCode}): {result.Item3.Trim()}");
        }

        return result;
    }

    static List<string> Lines(string text) => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    void Check(string what, bool passed)
    {
        if (!passed)
        {
            failures++;
        }

        Say($"{(passed ? "ok" : "FAILED")}: {what}");
    }

    void Say(string line) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,5:F0} s] {line}");
}

sealed class DrillException(string message) : Exception(message);
