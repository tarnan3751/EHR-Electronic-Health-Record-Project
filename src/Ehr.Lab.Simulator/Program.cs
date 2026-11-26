using System.Globalization;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Ehr.Lab.Simulator;

// The simulated lab's command line (the expansion plan's step 12, UC 20): a lab's system that asks the on-prem app for
// the lab orders waiting for results and files synthetic results for them (LabSimulator). From the repository folder,
// with the lab's key in EHR_LAB_KEY, never on the command line, where the shell's history would keep it:
//
//   dotnet run --project src/Ehr.Lab.Simulator -- --base-url https://ehr.example.com:8443
//
//   --certificate <file>       the on-prem app's certificate, trusted as it is, when the system doesn't trust it (the
//                              stack's: infra/compose/certs/example.com.pem); the address's name must still match
//   --poll-seconds <n>         how often it asks (5)
//   --min-delay-seconds <n>    how soon after an order is signed its results come (5)
//   --max-delay-seconds <n>    and how late (30)
//   --abnormal-rate <r>        how often a result is out of range for nothing on the patient's list (0.05)
//   --allow-http               let --base-url be http://, for testing against an app on this machine only
//   --idle-without-key         with no key, wait doing nothing instead of exiting (the stack's lab service, so that
//                              `docker compose up --wait` doesn't count a lab that isn't set up as a failure)
//
// Without a key it says so once and does nothing more. It never prints the key.
//
// An explicit Main rather than top-level statements: those would make a Program class here, which would hide the web
// app's own Program from the web tests, which run this in process.
static class Command
{
    const int Failed = 1;

    static async Task<int> Main(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name is "--allow-http" or "--idle-without-key")
            {
                options[name] = null;
            }
            else if (name is "--base-url" or "--certificate" or "--poll-seconds" or "--min-delay-seconds" or "--max-delay-seconds" or "--abnormal-rate"
                     && i + 1 < args.Length)
            {
                options[name] = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Unknown option {name}. See src/Ehr.Lab.Simulator/Program.cs for what it takes.");
                return Failed;
            }
        }

        using var stop = new CancellationTokenSource();
        void Stop(PosixSignalContext context)
        {
            context.Cancel = true;
            stop.Cancel();
        }

        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        var key = Environment.GetEnvironmentVariable("EHR_LAB_KEY");
        if (string.IsNullOrEmpty(key))
        {
            Console.WriteLine("EHR_LAB_KEY is not set, so the simulated lab is not running.");
            if (options.ContainsKey("--idle-without-key"))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            return 0;
        }

        // As the on-prem app's Ehr:Lab:Key.
        if (key.Length < 32)
        {
            Console.Error.WriteLine("EHR_LAB_KEY must be at least 32 characters, the same key as the on-prem app's Ehr:Lab:Key.");
            return Failed;
        }

        if (!options.TryGetValue("--base-url", out var address) || !Uri.TryCreate(address, UriKind.Absolute, out var baseUrl)
            || !(baseUrl.Scheme == Uri.UriSchemeHttps || (baseUrl.Scheme == Uri.UriSchemeHttp && options.ContainsKey("--allow-http"))))
        {
            Console.Error.WriteLine("Give --base-url, the on-prem app's https:// address. An http:// one is refused unless --allow-http is given, for testing on this machine only.");
            return Failed;
        }

        if (!Seconds(options, "--poll-seconds", 5, out var poll) || poll < TimeSpan.FromSeconds(1)
            || !Seconds(options, "--min-delay-seconds", 5, out var minimumDelay) || !Seconds(options, "--max-delay-seconds", 30, out var maximumDelay)
            || maximumDelay < minimumDelay
            || !double.TryParse(options.GetValueOrDefault("--abnormal-rate") ?? "0.05", NumberStyles.Float, CultureInfo.InvariantCulture, out var abnormalRate)
            || abnormalRate is < 0 or > 1)
        {
            Console.Error.WriteLine("The poll is at least 1 second and the delays at least 0, all whole seconds; the maximum delay is at least the minimum; and the abnormal rate is from 0 to 1.");
            return Failed;
        }

        // The on-prem app's certificate only, without its key: the file it serves, such as the stack's mkcert one.
        var pinned = options.GetValueOrDefault("--certificate") is { } path ? X509Certificate2.CreateFromPem(File.ReadAllText(path)) : null;
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            // Trusted by the system, or exactly the certificate given; the name must match either way.
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || (errors == SslPolicyErrors.RemoteCertificateChainErrors && certificate is not null && pinned is not null
                        && certificate.GetRawCertData().AsSpan().SequenceEqual(pinned.RawData)),
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(30) };
        // The lab's door's header (Ehr.Web's LabApi.KeyHeader).
        client.DefaultRequestHeaders.Add("X-Lab-Key", key);

        var lab = new LabSimulator(client, new LabSettings(minimumDelay, maximumDelay, abnormalRate), Console.WriteLine);
        Console.WriteLine($"The simulated lab asks {baseUrl} for lab orders every {poll.TotalSeconds:0} seconds.");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await lab.RunOnceAsync(DateTimeOffset.UtcNow, stop.Token);
                await Task.Delay(poll, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        Console.WriteLine("The simulated lab stopped.");
        return 0;
    }

    // A whole number of seconds from the option, or the fallback without it.
    static bool Seconds(Dictionary<string, string?> options, string name, int fallback, out TimeSpan value)
    {
        var parsed = int.TryParse(options.GetValueOrDefault(name) ?? fallback.ToString(CultureInfo.InvariantCulture), NumberStyles.None,
            CultureInfo.InvariantCulture, out var seconds);
        value = TimeSpan.FromSeconds(seconds);
        return parsed;
    }
}
