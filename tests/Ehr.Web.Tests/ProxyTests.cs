using System.Net;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// Proxy access (FR-19; the expansion plan's step 6): helpers for the test cases in TestCases/UC6 (a proxy chooses
// whose record to view) and TestCases/UC18 (giving and removing access). What the database refuses whatever the app
// does is in Ehr.Security.Tests (ProxyAccessSecurityTests).
static class ProxyTests
{
    // A patient with a confirmed portal account, signed in on the on-site app.
    public sealed record Person(Guid Patient, Guid Account, string Email, HttpClient Client, CookieContainer Jar);

    public static async Task<Person> PersonAsync(WebApps apps, string given, string family)
    {
        var patient = await apps.InsertPatientAsync(given, family);
        var (email, password) = await apps.CreatePatientAccountAsync(patient);
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);
        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        Assert.Equal("/portal", PathOf(signedIn));
        var account = Guid.Parse((await QueryAsync(apps, "SELECT id::text FROM ehr.patient_users WHERE patient_id = @patient", patient)).Single());
        return new Person(patient, account, email, client, jar);
    }

    // The patient gives access in the portal; the page it lands on.
    public static async Task<string> GiveAsync(Person patient, string email, string scope = "record", bool sensitive = false, int days = 90)
    {
        using var given = await PostFormAsync(patient.Client, "/portal/access", "/portal/access",
            ("Email", email), ("Scope", scope), ("IncludeSensitive", sensitive ? "true" : "false"), ("Days", days));
        return await given.Content.ReadAsStringAsync(Cancel);
    }

    // The proxy chooses the record; the page it lands on.
    public static async Task<string> ViewAsync(Person proxy, Guid grant)
    {
        using var chosen = await PostFormAsync(proxy.Client, "/portal/acting", "/portal/access", ("grantId", grant));
        Assert.Equal("/portal", PathOf(chosen));
        return await ReadAsync(chosen);
    }

    // The password every account these tests create signs in with (WebApps.CreatePatientAccountAsync uses it too).
    public const string Password = "a-patient-portal-password";

    // The invitation link in the latest email to that address: its path and query on this site.
    public static string InvitationPath(WebApps apps, string email)
    {
        var sent = apps.Emails.Sent.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));
        var link = new Uri(apps.Emails.Actions[sent.Body].Link);
        Assert.Equal("/portal/invitation", link.AbsolutePath);
        return link.PathAndQuery;
    }

    // Accepts an invitation as its page posts it; the page it answers with.
    public static async Task<string> AcceptAsync(WebApps apps, string invitation, string name, string password = Password, string? confirm = null)
    {
        var client = Client(apps.OnPrem, new CookieContainer());
        var form = await client.GetStringAsync(invitation, Cancel);
        if (!form.Contains("name=\"Input.Password\"", StringComparison.Ordinal))
        {
            // No form to post: the page already says why (used, expired or unknown).
            return form;
        }

        var code = System.Web.HttpUtility.ParseQueryString(invitation[(invitation.IndexOf('?') + 1)..])["code"]!;
        using var posted = await client.PostAsync("/portal/invitation", Form(("__RequestVerificationToken", Token(form)), ("Input.Code", code), ("Input.Name", name),
            ("Input.Password", password), ("Input.Confirm", confirm ?? password)), Cancel);
        return await posted.Content.ReadAsStringAsync(Cancel);
    }

    // Signs a portal account in on the on-site app; the client, its cookies, and where it landed.
    public static async Task<(HttpClient Client, CookieContainer Jar, string Path, string Page)> SignInAsync(WebApps apps, string email, string password = Password)
    {
        var jar = new CookieContainer();
        var client = Client(apps.OnPrem, jar);
        using var signedIn = await PostFormAsync(client, "/portal/sign-in", "/portal/sign-in", ("Input.Email", email), ("Input.Password", password));
        return (client, jar, PathOf(signedIn), await signedIn.Content.ReadAsStringAsync(Cancel));
    }

    public static async Task<Guid> AccountIdAsync(WebApps apps, string email)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT id FROM ehr.patient_users WHERE normalized_email = upper(@email)", db);
        command.Parameters.AddWithValue("email", email);
        return (Guid)(await command.ExecuteScalarAsync(Cancel))!;
    }

    // The grants for a patient's record, oldest first: "email scope sensitive state".
    public static Task<List<string>> GrantsAsync(WebApps apps, Guid patient) => QueryAsync(apps,
        """
        SELECT lower(proxy_email) || ' ' || scope || ' ' || include_sensitive || ' ' || granted_by_kind || ' '
            || CASE WHEN revoked_at IS NOT NULL THEN 'revoked' WHEN expires_at <= now() THEN 'expired' ELSE 'in-force' END
        FROM ehr.proxy_grants WHERE patient_id = @patient ORDER BY id
        """, patient);

    public static async Task<Guid> GrantIdAsync(WebApps apps, Guid patient) =>
        Guid.Parse((await QueryAsync(apps, "SELECT id::text FROM ehr.proxy_grants WHERE patient_id = @patient AND revoked_at IS NULL ORDER BY id DESC LIMIT 1", patient)).Single());

    public static async Task<List<string>> QueryAsync(WebApps apps, string sql, Guid patient)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("patient", patient);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
