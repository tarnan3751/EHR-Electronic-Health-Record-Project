using System.Diagnostics;
using Ehr.Seed;

// The synthetic clinic's command line (README.md, "Synthetic data"). From the repository folder:
//
//   dotnet run -c Release --project bench/Ehr.Bench -- seed [--patients 50000]
//
// loads the synthetic clinic into the database EHR_SEED_CONNECTION names, connecting as ehr_owner (Seeder). The
// connection string comes from the environment, so its password stays out of the command line and the repository.

const int DefaultPatients = 50_000;

if (args is not ["seed", .. var options] || !TryReadPatients(options, out var patients))
{
    Console.Error.WriteLine("Usage: dotnet run -c Release --project bench/Ehr.Bench -- seed [--patients 50000]");
    return 2;
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

static bool TryReadPatients(string[] options, out int patients)
{
    patients = DefaultPatients;
    return options switch
    {
        [] => true,
        ["--patients", var count] => int.TryParse(count, out patients) && patients > 0,
        _ => false,
    };
}
