// Sets up example test accounts on the local server stack (this folder) and prints their sign-in codes. For the local
// stack with synthetic data only: it refuses any address but the stack's, and the accounts must never exist on a
// server anyone else can reach. It drives the web app's own pages, as a person would, so it changes nothing about how
// signing in works: the bootstrap admin from .env, a clinician that admin invites at Staff, and a patient that
// clinician issues a portal enrollment code to.
//
//   dotnet run test-accounts.cs -- setup [options]   creates the accounts, or signs in with those set up already
//   dotnet run test-accounts.cs -- code              prints each account's email and password, and the current code
//
// The passwords are fixed example ones (Defaults, below), the same on every machine, so a class shares them. The
// authenticator secrets and recovery codes are made by the stack at enrollment and differ per machine: they live only
// in test-accounts.json beside this file, which is gitignored and, on macOS and Linux, readable only by its owner. The
// file is written as soon as a password or secret is live, so a later failure never loses one. setup never deletes or
// resets anything: an account the stack has but the file doesn't is reported, with what to do.

// Run, never published: without this, file-based apps default to native AOT and restore its compiler.
#:property PublishAot=false

using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Options.Usage);
    return 0;
}

Options options;
try
{
    options = Options.Parse(args.Skip(1).ToArray());
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

switch (args[0])
{
    case "setup":
        return await new Setup(options).RunAsync();
    case "code":
        return Codes.Print(options);
    default:
        Console.Error.WriteLine(Options.Usage);
        return 2;
}

// The example accounts. Local stack, synthetic data only.
static class Defaults
{
    public const string AdminPassword = "local-admin-test-1234";
    public const string ClinicianEmail = "clinician@example.com";
    public const string ClinicianName = "Dr. Test Clinician";
    public const string ClinicianPassword = "local-clinician-test-1234";
    public const string PatientEmail = "patient@example.com";
    public const string PatientPassword = "local-patient-test-1234";
    public const string PatientMrn = "SYN0000001";

    // Recorded with the emergency access a clinician gives to open the chart of a patient not on their panel.
    public const string Reason = "Setting up a test portal account on the local stack";

    // The app's rule for every password (AddEhrAccounts).
    public const int MinimumPasswordLength = 12;
}

sealed class Options
{
    public const string Usage = """
        Usage: dotnet run test-accounts.cs -- setup|code [options]
          --clinician-email <email>     default clinician@example.com
          --clinician-name <name>       default "Dr. Test Clinician"
          --clinician-password <pw>     default local-clinician-test-1234
          --patient-email <email>       default patient@example.com
          --patient-password <pw>       default local-patient-test-1234
          --mrn <mrn>                   the seeded patient to register, default SYN0000001
          --admin-password <pw>         the admin's new password at its first sign-in, default local-admin-test-1234
          --clinical-url <url>          default https://ehr.example.com:8443
          --portal-url <url>            default https://portal.example.com:9443
          --mailpit-url <url>           default http://127.0.0.1:8025
          --env-file <path>             default .env beside this script
          --ca <path>                   default certs/rootCA.pem beside this script
          --accounts <path>             default test-accounts.json beside this script
        """;

    // The local stack's names (the hosts-file line) and this machine.
    static readonly string[] StackHosts = ["ehr.example.com", "ehr-remote.example.com", "portal.example.com", "localhost", "127.0.0.1"];

    public Uri Clinical { get; private set; } = new("https://ehr.example.com:8443");
    public Uri Portal { get; private set; } = new("https://portal.example.com:9443");
    public Uri Mailpit { get; private set; } = new("http://127.0.0.1:8025");
    public string EnvFile { get; private set; } = Path.Combine(Here(), ".env");
    public string RootCertificate { get; private set; } = Path.Combine(Here(), "certs", "rootCA.pem");
    public string AccountsFile { get; private set; } = Path.Combine(Here(), "test-accounts.json");
    public string AdminPassword { get; private set; } = Defaults.AdminPassword;
    public string ClinicianEmail { get; private set; } = Defaults.ClinicianEmail;
    public string ClinicianName { get; private set; } = Defaults.ClinicianName;
    public string ClinicianPassword { get; private set; } = Defaults.ClinicianPassword;
    public string PatientEmail { get; private set; } = Defaults.PatientEmail;
    public string PatientPassword { get; private set; } = Defaults.PatientPassword;
    public string Mrn { get; private set; } = Defaults.PatientMrn;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i += 2)
        {
            var value = i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--clinician-email": options.ClinicianEmail = value; break;
                case "--clinician-name": options.ClinicianName = value; break;
                case "--clinician-password": options.ClinicianPassword = Password(args[i], value); break;
                case "--patient-email": options.PatientEmail = value; break;
                case "--patient-password": options.PatientPassword = Password(args[i], value); break;
                case "--mrn": options.Mrn = value; break;
                case "--admin-password": options.AdminPassword = Password(args[i], value); break;
                case "--clinical-url": options.Clinical = Local(args[i], value, https: true); break;
                case "--portal-url": options.Portal = Local(args[i], value, https: true); break;
                case "--mailpit-url": options.Mailpit = Local(args[i], value, https: false); break;
                case "--env-file": options.EnvFile = Path.GetFullPath(value); break;
                case "--ca": options.RootCertificate = Path.GetFullPath(value); break;
                case "--accounts": options.AccountsFile = Path.GetFullPath(value); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return options;
    }

    static string Password(string option, string value) => value.Length >= Defaults.MinimumPasswordLength
        ? value
        : throw new ArgumentException($"{option}: the app needs at least {Defaults.MinimumPasswordLength} characters.");

    // The sites are HTTPS only; Mailpit's API is plain HTTP on this machine.
    static Uri Local(string option, string value, bool https) =>
        !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !StackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            ? throw new ArgumentException($"{option}: {value} isn't the local stack. This script is for the local stack only.")
            : https && uri.Scheme != Uri.UriSchemeHttps
                ? throw new ArgumentException($"{option}: the stack's sites are HTTPS only.")
                : uri;

    // This script's folder, wherever it is run from.
    static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}

sealed class Setup(Options options)
{
    AccountsFile accounts = new();
    X509Certificate2 root = null!;
    bool failed;

    string FileName => Path.GetFileName(options.AccountsFile);

    string ResetAdvice => $"reset the stack (docker compose --profile \"*\" down -v, which deletes all its data, the seeded " +
        $"clinic included), delete {FileName}, and seed the clinic again";

    // An admin's reset makes the account enroll again at its next sign-in, which setup does with the password in the file.
    string AuthenticatorAdvice => "An admin can reset its authenticator at Staff, and the next setup enrolls it again; " +
        $"or, to start again, {ResetAdvice}.";

    public async Task<int> RunAsync()
    {
        if (!File.Exists(options.RootCertificate))
        {
            Console.Error.WriteLine($"No certificate authority at {options.RootCertificate}. Make the certificates first " +
                "(infra/compose/README.md, One-time setup), or pass --ca <path to rootCA.pem>.");
            return 1;
        }

        root = X509Certificate2.CreateFromPem(File.ReadAllText(options.RootCertificate));
        accounts = AccountsFile.Load(options.AccountsFile);
        Console.WriteLine($"Stack: {options.Clinical.GetLeftPart(UriPartial.Authority)} (clinical), {options.Portal.GetLeftPart(UriPartial.Authority)} (portal), " +
            $"{options.Mailpit.GetLeftPart(UriPartial.Authority)} (Mailpit)");
        Console.WriteLine($"Accounts file: {options.AccountsFile}");

        try
        {
            using var probe = new Browser(root);
            await probe.GetAsync(Clinical("/sign-in"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"The stack doesn't answer at {options.Clinical}: {Explain(e)}");
            return 1;
        }

        using var admin = await RunAsync("admin", accounts.Find("admin")?.Email ?? "(bootstrap admin)", EnsureAdminAsync);

        Browser? clinician = null;
        foreach (var saved in accounts.All("clinician").Where(a => !Same(a.Email, options.ClinicianEmail)).ToList())
        {
            (await RunAsync("clinician", saved.Email, () => VerifyStaffAsync(saved, "/patients")))?.Dispose();
        }

        clinician = await RunAsync("clinician", options.ClinicianEmail, () => EnsureClinicianAsync(admin));

        foreach (var saved in accounts.All("patient").Where(a => !Same(a.Email, options.PatientEmail)).ToList())
        {
            (await RunAsync("patient", saved.Email, () => VerifyPatientAsync(saved)))?.Dispose();
        }

        (await RunAsync("patient", options.PatientEmail, () => EnsurePatientAsync(clinician)))?.Dispose();
        clinician?.Dispose();

        Console.WriteLine(failed
            ? "Not every account is ready; see above."
            : "Done. dotnet run test-accounts.cs -- code prints the sign-in details and the current codes.");
        return failed ? 1 : 0;
    }

    // One account's step: what it did, or why it couldn't. A failure stops this account, not the others.
    async Task<Browser?> RunAsync(string role, string email, Func<Task<(Browser Session, string Done)>> step)
    {
        try
        {
            var (session, done) = await step();
            Console.WriteLine($"{role,-10} {email,-26} {done}");
            return session;
        }
        catch (Skipped e)
        {
            Console.WriteLine($"{role,-10} {email,-26} skipped: {e.Message}");
        }
        catch (Exception e) when (e is Failure or HttpRequestException or TaskCanceledException)
        {
            failed = true;
            Console.WriteLine($"{role,-10} {email,-26} FAILED: {(e is Failure ? e.Message : Explain(e))}");
        }

        return null;
    }

    async Task<(Browser, string)> EnsureAdminAsync()
    {
        if (accounts.Find("admin") is { } saved)
        {
            return await VerifyStaffAsync(saved, "/admin/staff");
        }

        var env = ReadEnv();
        var email = env.GetValueOrDefault("EHR_BOOTSTRAP_ADMIN_EMAIL");
        var password = env.GetValueOrDefault("EHR_BOOTSTRAP_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        {
            throw new Failure($"{options.EnvFile} has no EHR_BOOTSTRAP_ADMIN_EMAIL or EHR_BOOTSTRAP_ADMIN_PASSWORD (see .env.example).");
        }

        var browser = new Browser(root);
        var page = await SignInAsync(browser, email, password);
        Account account;
        switch (page.Path)
        {
            case "/sign-in/password":
                page = await browser.PostAsync(Clinical("/sign-in/password"), page,
                    ("Input.Password", options.AdminPassword), ("Input.Confirm", options.AdminPassword));
                Expect(page, "/sign-in/enroll", "setting the admin's new password");
                account = accounts.Add(new() { Role = "admin", Email = email, Password = options.AdminPassword });
                Save();
                break;
            case "/sign-in/enroll":
                account = accounts.Add(new() { Role = "admin", Email = email, Password = password });
                Save();
                break;
            case "/sign-in/code" or "/sign-in":
                // Someone signed in as the bootstrap admin by hand: its password or authenticator is theirs, not the file's.
                throw new Failure($"the bootstrap admin {email} was set up by hand already, and {FileName} doesn't have it, so " +
                    $"setup can't take it over. To start again, {ResetAdvice}, and run setup before anyone signs in as the admin.");
            default:
                throw Unexpected(page, "the admin's first sign-in");
        }

        await EnrollAsync(browser, page, account);
        Expect(await browser.GetAsync(Clinical("/admin/staff")), "/admin/staff", "opening Staff as the admin");
        return (browser, "created: new password set, authenticator enrolled");
    }

    async Task<(Browser, string)> EnsureClinicianAsync(Browser? admin)
    {
        var email = options.ClinicianEmail;
        if (accounts.Find("clinician", email) is { } saved)
        {
            return await VerifyStaffAsync(saved, "/patients");
        }

        if (admin is null)
        {
            throw new Failure("needs the admin to invite it, and the admin isn't signed in (above).");
        }

        var staff = await admin.GetAsync(Clinical("/admin/staff"));
        Expect(staff, "/admin/staff", "opening Staff as the admin");
        if (StaffStatus(staff, email) is { } status)
        {
            throw new Failure($"the stack has this staff account already ({status}), but {FileName} doesn't, so setup can't sign " +
                $"in as it. Choose another address with --clinician-email; or, to start again, {ResetAdvice}.");
        }

        var invited = await admin.PostAsync(Clinical("/admin/staff"), staff, ("Input.Email", email),
            ("Input.DisplayName", options.ClinicianName), ("Input.Clinician", "true"), ("Input.Admin", "false"));
        var link = Regex.Match(invited.Html, @"<p class=""sc-copy"">\s*(https?://[^<\s]+/sign-in/setup\?[^<\s]+)");
        if (!link.Success)
        {
            throw Unexpected(invited, "inviting the clinician at Staff");
        }

        // The link carries the stack's configured address; this run's address reaches the same page.
        var setup = Clinical(new Uri(WebUtility.HtmlDecode(link.Groups[1].Value)).PathAndQuery);
        var browser = new Browser(root);
        var page = await browser.PostAsync(setup, await browser.GetAsync(setup),
            ("Input.Password", options.ClinicianPassword), ("Input.Confirm", options.ClinicianPassword));
        Expect(page, "/sign-in", "setting the clinician's password with its setup link");
        var account = accounts.Add(new() { Role = "clinician", Email = email, Password = options.ClinicianPassword });
        Save();

        page = await SignInAsync(browser, email, options.ClinicianPassword);
        Expect(page, "/sign-in/enroll", "the clinician's first sign-in");
        await EnrollAsync(browser, page, account);
        Expect(await browser.GetAsync(Clinical("/patients")), "/patients", "opening the patient search as the clinician");
        return (browser, "created: invited at Staff, password set, authenticator enrolled, patient search opens");
    }

    async Task<(Browser, string)> EnsurePatientAsync(Browser? clinician)
    {
        var email = options.PatientEmail;
        var mrn = options.Mrn;
        if (accounts.Find("patient", email) is { } saved)
        {
            if (saved.Mrn != mrn)
            {
                throw new Failure($"{FileName} has this address for MRN {saved.Mrn}; for MRN {mrn}, choose another address with --patient-email.");
            }

            return await VerifyPatientAsync(saved);
        }

        if (accounts.All("patient").FirstOrDefault(a => a.Mrn == mrn) is { } other)
        {
            throw new Failure($"MRN {mrn} has a portal account in {FileName} already, as {other.Email}. Choose another patient with --mrn.");
        }

        if (clinician is null)
        {
            throw new Failure("needs the clinician to issue an enrollment code, and the clinician isn't signed in (above).");
        }

        var before = await MailAsync(email);
        var results = await clinician.GetAsync(Clinical($"/patients?mrn={Uri.EscapeDataString(mrn)}"));
        Expect(results, "/patients", "searching for the patient by MRN");
        var chartLink = ChartLink(results, mrn) ?? throw new Skipped($"the clinic has no patient with MRN {mrn}. Seed the " +
            "synthetic clinic first (README, \"Synthetic data and the benchmark\"), then run setup again.");

        var chart = await clinician.GetAsync(Clinical(chartLink));
        if (chart.Path.EndsWith("/reason", StringComparison.Ordinal))
        {
            chart = await clinician.PostAsync(chart.Url, chart, ("Reason", Defaults.Reason));
        }

        if (FormAction(chart, "Assign") is { } assign)
        {
            chart = await clinician.PostAsync(Clinical(assign), chart);
        }

        var enroll = FormAction(chart, "Enroll") ?? throw Unexpected(chart, "opening the patient's chart");
        var issued = await clinician.PostAsync(Clinical(enroll), chart);
        var code = Regex.Match(issued.Html, @"<p class=""mono-code"">\s*([^<]+?)\s*</p>");
        if (!code.Success)
        {
            throw Unexpected(issued, "issuing an enrollment code");
        }

        var portal = new Browser(root);
        var page = await portal.PostAsync(Portal("/portal/register"), await portal.GetAsync(Portal("/portal/register")),
            ("Input.Code", WebUtility.HtmlDecode(code.Groups[1].Value)), ("Input.Email", email),
            ("Input.Password", options.PatientPassword), ("Input.Confirm", options.PatientPassword));
        if (!page.Html.Contains("Check your email", StringComparison.Ordinal))
        {
            throw page.Html.Contains("enrollment code is not valid", StringComparison.Ordinal)
                ? new Failure($"MRN {mrn} has a portal account on the stack already (the code issued just now was refused), but " +
                    $"{FileName} doesn't have it, so setup can't sign in as it. Choose another patient with --mrn and another " +
                    $"address with --patient-email; or, to start again, {ResetAdvice}.")
                : Unexpected(page, "registering at the portal");
        }

        var account = accounts.Add(new() { Role = "patient", Email = email, Password = options.PatientPassword, Mrn = mrn });
        Save();
        await ConfirmAsync(portal, email, before);
        await PortalSignInAsync(portal, account);
        return (portal, $"created: enrollment code issued for MRN {mrn}, registered, email confirmed, portal opens");
    }

    // An account from the file, checked by signing in with what the file holds.
    async Task<(Browser, string)> VerifyStaffAsync(Account account, string home)
    {
        var browser = new Browser(root);
        var page = await SignInAsync(browser, account.Email, account.Password);
        var done = "ok: signed in with its password and authenticator code";
        switch (page.Path)
        {
            case "/sign-in/code" when account.AuthenticatorSecret is { } secret:
                page = await browser.PostAsync(Clinical("/sign-in/code"), page, ("Code", Totp.Code(secret, DateTimeOffset.UtcNow)));
                if (page.Path.StartsWith("/sign-in", StringComparison.Ordinal))
                {
                    throw new Failure($"the authenticator code from {FileName} was refused. Check this computer's clock; otherwise " +
                        $"the secret in the file isn't this account's. {AuthenticatorAdvice}");
                }

                break;
            case "/sign-in/code":
                throw new Failure($"the stack asks for its authenticator code, but {FileName} has no authenticator secret for it. {AuthenticatorAdvice}");
            case "/sign-in/enroll":
                // Its authenticator was reset at Staff, or its enrollment never finished: it enrolls, as at a first sign-in.
                await EnrollAsync(browser, page, account);
                done = "ok: signed in, and enrolled the authenticator it was missing";
                break;
            case "/sign-in":
                throw new Failure($"can't sign in with the password in {FileName} ({string.Join(" ", page.Problems)}). If the stack " +
                    $"was reset (down -v), its accounts and the seeded clinic went with it: delete {FileName}, seed the clinic, and run setup again.");
            default:
                throw Unexpected(page, "signing in");
        }

        Expect(await browser.GetAsync(Clinical(home)), home, $"opening {home}");
        return (browser, done);
    }

    async Task<(Browser, string)> VerifyPatientAsync(Account account)
    {
        var portal = new Browser(root);
        await PortalSignInAsync(portal, account);
        return (portal, $"ok: signed in to the portal, which shows MRN {account.Mrn}");
    }

    async Task PortalSignInAsync(Browser portal, Account account)
    {
        var page = await PortalPasswordAsync(portal, account);
        if (page.Html.Contains("Confirm your email address first", StringComparison.Ordinal))
        {
            // Registered on an earlier run that stopped before confirming: the email is still in Mailpit.
            await ConfirmAsync(portal, account.Email, new HashSet<string>());
            page = await PortalPasswordAsync(portal, account);
        }

        if (page.Path != "/portal")
        {
            throw new Failure($"can't sign in to the portal with the password in {FileName} ({string.Join(" ", page.Problems)}). " +
                $"If the stack was reset (down -v), its accounts and the seeded clinic went with it: delete {FileName}, seed the clinic, and run setup again.");
        }

        if (account.Mrn is { } mrn && !page.Html.Contains($"<dd>{mrn}</dd>", StringComparison.Ordinal))
        {
            throw new Failure($"signs in, but the portal doesn't show MRN {mrn}.");
        }
    }

    async Task<Page> PortalPasswordAsync(Browser portal, Account account) =>
        await portal.PostAsync(Portal("/portal/sign-in"), await portal.GetAsync(Portal("/portal/sign-in")),
            ("Input.Email", account.Email), ("Input.Password", account.Password));

    async Task<Page> SignInAsync(Browser browser, string email, string password) =>
        await browser.PostAsync(Clinical("/sign-in"), await browser.GetAsync(Clinical("/sign-in")),
            ("Input.Email", email), ("Input.Password", password));

    // The enrollment page shows the new key; a code from it finishes enrollment, and the recovery codes are shown once.
    async Task EnrollAsync(Browser browser, Page page, Account account)
    {
        var secret = Regex.Match(page.Html, "secret=([A-Z2-7]+)");
        if (!secret.Success)
        {
            throw Unexpected(page, "enrolling an authenticator");
        }

        // Saved first, so a key the stack accepts is never lost. If enrollment doesn't finish, the next sign-in offers a
        // new key, which setup then enrolls.
        account.AuthenticatorSecret = secret.Groups[1].Value;
        Save();
        var done = await browser.PostAsync(Clinical("/sign-in/enroll"), page, ("Code", Totp.Code(secret.Groups[1].Value, DateTimeOffset.UtcNow)));
        if (!done.Html.Contains("Your recovery codes", StringComparison.Ordinal))
        {
            throw Unexpected(done, "enrolling an authenticator");
        }

        account.RecoveryCodes = [.. Regex.Matches(done.Html, @"<li class=""mono-code"">\s*([^<]+?)\s*</li>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))];
        Save();
    }

    // The confirmation link from the newest registration email in Mailpit that wasn't there before, then its button.
    async Task ConfirmAsync(Browser portal, string email, IReadOnlySet<string> before)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            foreach (var id in (await MailAsync(email)).Where(id => !before.Contains(id)))
            {
                using var message = JsonDocument.Parse(await MailpitAsync($"api/v1/message/{id}"));
                var link = Regex.Match(message.RootElement.GetProperty("Text").GetString() ?? "", @"https?://\S+/portal/confirm\?\S+");
                if (link.Success)
                {
                    var confirm = Portal(new Uri(link.Value).PathAndQuery);
                    var page = await portal.PostAsync(confirm, await portal.GetAsync(confirm));
                    if (!page.Html.Contains("Email address confirmed", StringComparison.Ordinal))
                    {
                        throw Unexpected(page, "confirming the email address");
                    }

                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new Failure($"no confirmation email for {email} reached Mailpit ({options.Mailpit}) within 30 seconds.");
    }

    // The IDs of the emails Mailpit holds for an address, newest first.
    async Task<HashSet<string>> MailAsync(string email)
    {
        string json;
        try
        {
            json = await MailpitAsync("api/v1/messages?limit=100");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new Failure($"Mailpit doesn't answer at {options.Mailpit} ({Explain(e)}), so the confirmation email can't be read.");
        }

        using var list = JsonDocument.Parse(json);
        return [.. list.RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("To").EnumerateArray().Any(to => Same(to.GetProperty("Address").GetString(), email)))
            .Select(m => m.GetProperty("ID").GetString()!)];
    }

    async Task<string> MailpitAsync(string path)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        return await client.GetStringAsync(new Uri(options.Mailpit, path));
    }

    Dictionary<string, string> ReadEnv()
    {
        if (!File.Exists(options.EnvFile))
        {
            throw new Failure($"no {options.EnvFile}: copy .env.example to .env (infra/compose/README.md, One-time setup), or pass --env-file <path>.");
        }

        return File.ReadAllLines(options.EnvFile)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim().Trim('"', '\''));
    }

    void Save() => accounts.Save(options.AccountsFile);

    Uri Clinical(string pathAndQuery) => new(options.Clinical, pathAndQuery);

    Uri Portal(string pathAndQuery) => new(options.Portal, pathAndQuery);

    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static void Expect(Page page, string path, string step)
    {
        if (page.Path != path)
        {
            throw Unexpected(page, step);
        }
    }

    static Failure Unexpected(Page page, string step) =>
        new($"{step} landed on {page.Path} ({(int)page.Status}, \"{page.Heading}\"){(page.Problems.Count > 0 ? ": " + string.Join(" ", page.Problems) : "")}, not the page expected.");

    // The status of a staff account on the Staff page, or null when it has none.
    static string? StaffStatus(Page staff, string email) => Regex
        .Matches(staff.Html, @"<tr>\s*<td>(.*?)</td>\s*<td>(.*?)</td>\s*<td>(.*?)</td>\s*<td>(.*?)</td>", RegexOptions.Singleline)
        .Where(row => Same(WebUtility.HtmlDecode(row.Groups[2].Value).Trim(), email))
        .Select(row => WebUtility.HtmlDecode(row.Groups[4].Value).Trim())
        .FirstOrDefault();

    // The chart link of the search result whose MRN is exactly this one.
    static string? ChartLink(Page results, string mrn) => Regex
        .Matches(results.Html, @"<tr>(.*?)</tr>", RegexOptions.Singleline)
        .Where(row => Regex.IsMatch(row.Groups[1].Value, $@"<td[^>]*>{Regex.Escape(mrn)}</td>"))
        .Select(row => Regex.Match(row.Groups[1].Value, @"href=""(/patients/[0-9a-f-]{36}[^""]*)"""))
        .Where(link => link.Success)
        .Select(link => WebUtility.HtmlDecode(link.Groups[1].Value))
        .FirstOrDefault();

    // Where the chart's form for a page handler posts, or null when the chart doesn't offer it.
    static string? FormAction(Page chart, string handler)
    {
        var form = Regex.Match(chart.Html, $@"<form[^>]*action=""([^""]*handler={handler}[^""]*)""");
        return form.Success ? WebUtility.HtmlDecode(form.Groups[1].Value) : null;
    }

    static string Explain(Exception e) => e switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } =>
            "the name doesn't resolve. Add the hosts-file line (infra/compose/README.md, One-time setup).",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } =>
            "nothing answers there. Is the stack running? (docker compose up -d, in infra/compose)",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } =>
            "its certificate isn't one the stack's rootCA.pem vouches for (infra/compose/README.md, One-time setup).",
        TaskCanceledException => "it didn't answer in time.",
        _ => e.Message,
    };
}

static class Codes
{
    public static int Print(Options options)
    {
        if (!File.Exists(options.AccountsFile))
        {
            Console.Error.WriteLine($"No {options.AccountsFile}: run setup first.");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        Console.WriteLine($"Staff sign in at {options.Clinical.GetLeftPart(UriPartial.Authority)}, patients at {new Uri(options.Portal, "/portal/sign-in")}. " +
            $"Codes change every 30 seconds; these work for {Totp.SecondsLeft(now)} more.");
        foreach (var account in AccountsFile.Load(options.AccountsFile).Accounts)
        {
            var code = account.AuthenticatorSecret is { } secret ? $"code {Totp.Code(secret, now)}" : account.Mrn is { } mrn ? $"MRN {mrn}" : "";
            Console.WriteLine($"{account.Role,-10} {account.Email,-26} {account.Password,-28} {code}");
        }

        return 0;
    }
}

// RFC 6238 time-based codes, as authenticator apps make them: HMAC-SHA1, 30-second steps, six digits.
static class Totp
{
    public static string Code(string secret, DateTimeOffset at)
    {
        Span<byte> step = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(step, at.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(Base32(secret), step);
        var offset = hash[^1] & 0xF;
        return ((BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF) % 1_000_000).ToString("D6");
    }

    public static int SecondsLeft(DateTimeOffset at) => 30 - (int)(at.ToUnixTimeSeconds() % 30);

    static byte[] Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c is not ('=' or ' ')))
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. bytes];
    }
}

// A browser for the script: cookies, redirects followed, no proxy, and HTTPS checked against the stack's own root
// certificate, whatever the operating system trusts. The host name must match, as always.
sealed class Browser : IDisposable
{
    readonly HttpClient client;

    public Browser(X509Certificate2 root)
    {
        // The stack is on this machine: a system proxy would only get in the way.
        var handler = new SocketsHttpHandler { CookieContainer = new CookieContainer(), ConnectTimeout = TimeSpan.FromSeconds(10), UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is not X509Certificate2 presented || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            {
                return false;
            }

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(presented);
        };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<Page> GetAsync(Uri url)
    {
        using var response = await client.GetAsync(url);
        return await Page.ReadAsync(response);
    }

    // Posts a page's form, with the page's antiforgery token.
    public async Task<Page> PostAsync(Uri url, Page form, params (string Name, string Value)[] fields)
    {
        var token = Regex.Match(form.Html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        if (!token.Success)
        {
            throw new Failure($"{form.Path} has no form to post (\"{form.Heading}\").");
        }

        using var content = new FormUrlEncodedContent(
            [.. fields.Select(f => KeyValuePair.Create(f.Name, f.Value)), KeyValuePair.Create("__RequestVerificationToken", token.Groups[1].Value)]);
        using var response = await client.PostAsync(url, content);
        return await Page.ReadAsync(response);
    }

    public void Dispose() => client.Dispose();
}

// Where a request ended up, after redirects, and what it showed.
sealed record Page(Uri Url, HttpStatusCode Status, string Html)
{
    public string Path => Url.AbsolutePath;

    public string Heading => Text(Regex.Match(Html, @"<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline).Groups[1].Value);

    // The errors the page shows: the validation summary's, each field's, and an error notice.
    public IReadOnlyList<string> Problems =>
        [.. Regex.Matches(Html, @"validation-summary-errors[^>]*>(.*?)</div>|field-validation-error[^>]*>(.*?)</span>|class=""sc-notice sc-notice--danger""[^>]*>(.*?)</(?:p|ul)>", RegexOptions.Singleline)
            .Select(m => Text(m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value))
            .Where(text => text.Length > 0)];

    public static async Task<Page> ReadAsync(HttpResponseMessage response) =>
        new(response.RequestMessage!.RequestUri!, response.StatusCode, await response.Content.ReadAsStringAsync());

    static string Text(string html) => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, "<[^>]+>", " "), @"\s+", " ")).Trim();
}

sealed class Failure(string message) : Exception(message);

// Not a failure: the stack lacks what the step needs, and says what to do.
sealed class Skipped(string message) : Exception(message);

// test-accounts.json: each account's email and password, and a staff account's authenticator secret and recovery codes.
sealed class AccountsFile
{
    public List<Account> Accounts { get; set; } = [];

    public Account? Find(string role, string? email = null) =>
        Accounts.FirstOrDefault(a => a.Role == role && (email is null || string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));

    public IEnumerable<Account> All(string role) => Accounts.Where(a => a.Role == role);

    public Account Add(Account account)
    {
        Accounts.Add(account);
        return account;
    }

    public static AccountsFile Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AccountsJson.Default.AccountsFile) ?? new() : new();

    // Written whole to a new file, readable only by its owner on macOS and Linux, which then replaces the old one.
    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.Delete(temporary);
        var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temporary, create))
        {
            JsonSerializer.Serialize(stream, this, AccountsJson.Default.AccountsFile);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

sealed class Account
{
    public string Role { get; set; } = "";
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string? AuthenticatorSecret { get; set; }
    public List<string>? RecoveryCodes { get; set; }
    public string? Mrn { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AccountsFile))]
sealed partial class AccountsJson : JsonSerializerContext
{
}
