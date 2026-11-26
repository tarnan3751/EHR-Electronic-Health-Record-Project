// After a deployment or an upgrade, checks the three sites as a browser reaches them (infra/deploy/README.md). From the
// repository folder, on any machine with .NET 10 that can reach them:
//
//   dotnet run infra/deploy/check-sites.cs -- --clinic <url> --remote <url> --portal <url> [--ca <file>] [--connect <address>] [--days <n>]
//
//   --clinic   the clinic's staff site (the on-prem app), such as https://ehr.example.com
//   --remote   the off-site staff site (the cloud app), such as https://ehr-remote.example.com
//   --portal   the patient portal (the cloud app), such as https://portal.example.com
//   --ca       a root certificate (PEM) to trust instead of the system's, such as the local stack's certs/rootCA.pem
//   --connect  an address to connect to for every site, keeping each site's name for TLS and the request, as curl's
//              --resolve does: 127.0.0.1 for the local stack, where the names aren't in DNS
//   --days     how many days each certificate must still have left (default 14)
//
// Each site must answer over HTTPS with a certificate valid for its name: the clinic's home sends an anonymous visitor to
// sign in (302) and its sign-in page loads (200); the off-site sign-in page and the portal's load (200). Every page opens
// a database transaction, so a 200 also means that site's database answers. Nothing is sent but these GETs, no redirect
// is followed, and it signs nobody in. Exits with 1, naming each check that failed. CI's stack job runs it against the
// local stack on every push.

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

string? clinic = null, remote = null, portal = null, caFile = null, connect = null;
var days = 14;
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    try
    {
        switch (args[i])
        {
            case "--clinic": clinic = Next(); break;
            case "--remote": remote = Next(); break;
            case "--portal": portal = Next(); break;
            case "--ca": caFile = Next(); break;
            case "--connect": connect = Next(); break;
            case "--days": days = int.Parse(Next()); break;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
}

if (clinic is null || remote is null || portal is null)
{
    Console.Error.WriteLine("Give --clinic, --remote and --portal: see the header of infra/deploy/check-sites.cs.");
    return 2;
}

var root = caFile is null ? null : X509Certificate2.CreateFromPem(File.ReadAllText(caFile));
var failures = 0;

// The certificate each request was answered with, and what was wrong with it, by host.
var seen = new Dictionary<string, (X509Certificate2 Certificate, string? Problem)>();

using var client = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    UseProxy = false,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    ConnectCallback = async (context, cancellationToken) =>
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            var port = context.DnsEndPoint.Port;
            await (connect is null
                ? socket.ConnectAsync(context.DnsEndPoint, cancellationToken)
                : socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(connect), port), cancellationToken));
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    },
    SslOptions =
    {
        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
        {
            var host = ((SslStream)sender).TargetHostName;
            if (certificate is null)
            {
                return false;
            }

            var served = new X509Certificate2(certificate);
            string? problem = null;
            if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                problem = "isn't for this name";
            }
            else if (root is null ? errors != SslPolicyErrors.None : !TrustedBy(served, root))
            {
                problem = root is null ? "isn't trusted by this machine" : $"isn't signed by {caFile}";
            }

            seen[host] = (served, problem);
            return problem is null;
        },
    },
})
{ Timeout = TimeSpan.FromSeconds(30) };

await CheckAsync(clinic, "/", HttpStatusCode.Redirect, "The clinic's home sends an anonymous visitor to sign in");
await CheckAsync(clinic, "/sign-in", HttpStatusCode.OK, "The clinic's sign-in page loads");
await CheckAsync(remote, "/sign-in", HttpStatusCode.OK, "The off-site sign-in page loads");
await CheckAsync(portal, "/portal/sign-in", HttpStatusCode.OK, "The portal's sign-in page loads");

// The days left on each certificate that was otherwise good: one that wasn't has failed its site's checks already.
foreach (var (host, (certificate, _)) in seen.Where(s => s.Value.Problem is null))
{
    var left = certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow;
    Report(left >= TimeSpan.FromDays(days), $"{host}'s certificate has {left.TotalDays:F0} days left (at least {days} wanted)");
}

Console.WriteLine(failures == 0 ? "Every site checks out." : $"{failures} check(s) failed.");
return failures == 0 ? 0 : 1;

async Task CheckAsync(string site, string path, HttpStatusCode expected, string what)
{
    var url = new Uri(new Uri(site), path);
    if (url.Scheme != Uri.UriSchemeHttps)
    {
        Report(false, $"{what}: {url} isn't https://");
        return;
    }

    try
    {
        using var response = await client.GetAsync(url);
        Report(response.StatusCode == expected, $"{what}: {url} answered {(int)response.StatusCode} (expected {(int)expected})");
    }
    catch (HttpRequestException e)
    {
        var problem = seen.TryGetValue(url.Host, out var tls) && tls.Problem is { } p ? $"its certificate {p}" : e.Message;
        Report(false, $"{what}: {url} didn't answer: {problem}");
    }
    catch (TaskCanceledException)
    {
        Report(false, $"{what}: {url} didn't answer within 30 seconds");
    }
}

static bool TrustedBy(X509Certificate2 served, X509Certificate2 root)
{
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(root);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    return chain.Build(served);
}

void Report(bool passed, string what)
{
    if (!passed)
    {
        failures++;
    }

    Console.WriteLine($"{(passed ? "ok" : "FAILED")}: {what}");
}
