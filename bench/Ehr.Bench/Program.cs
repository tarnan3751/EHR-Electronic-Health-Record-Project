using System.Diagnostics;
using Ehr.Seed;

namespace Ehr.Bench;

// The synthetic clinic and the render-time benchmark (README.md, "Synthetic data and the benchmark"). From the
// repository folder:
//
//   dotnet run -c Release --project bench/Ehr.Bench -- seed [--patients 50000]
//       loads the synthetic clinic into the database EHR_SEED_CONNECTION names, connecting as ehr_owner (Seeder). The
//       connection string comes from the environment, so its password stays out of the command line and the repository.
//
//   dotnet run -c Release --project bench/Ehr.Bench -- [--patients 50000] [--stack] [--assert]
//       measures the search and chart pages' server render time (RenderBenchmark), in a PostgreSQL 18 it starts and
//       seeds itself, or with --stack against a primary already seeded with that many patients. --assert exits with 1
//       when a p99 excluding the commit is 20 ms or more.
//
// An explicit Main rather than top-level statements: those would make a Program class here, which would hide the web
// app's own Program, the one WebApplicationFactory starts.

static class Command
{
    const int DefaultPatients = 50_000;

    const string Usage = """
        Usage: dotnet run -c Release --project bench/Ehr.Bench -- seed [--patients 50000]
               dotnet run -c Release --project bench/Ehr.Bench -- [--patients 50000] [--stack] [--assert]
        """;

    static async Task<int> Main(string[] args)
    {
        var seed = args is ["seed", ..];
        var patients = DefaultPatients;
        var (stack, assert) = (false, false);
        var options = seed ? args[1..] : args;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--patients" when i + 1 < options.Length && int.TryParse(options[i + 1], out patients) && patients > 0:
                    i++;
                    break;
                case "--stack" when !seed:
                    stack = true;
                    break;
                case "--assert" when !seed:
                    assert = true;
                    break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (!seed)
        {
            return await RenderBenchmark.RunAsync(patients, stack, assert);
        }

        var connectionString = Environment.GetEnvironmentVariable("EHR_SEED_CONNECTION");
        if (connectionString is not { Length: > 0 })
        {
            Console.Error.WriteLine("Set EHR_SEED_CONNECTION to a connection string for ehr_owner (see README.md).");
            return 2;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var rows = await Seeder.LoadAsync(connectionString, new SyntheticClinic(patients), CancellationToken.None);
            Console.WriteLine($"Seeded {patients:N0} synthetic patients in {clock.Elapsed.TotalSeconds:F1} s:");
            foreach (var (table, count) in rows)
            {
                Console.WriteLine($"  {table,-24}{count,10:N0}");
            }

            return 0;
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
