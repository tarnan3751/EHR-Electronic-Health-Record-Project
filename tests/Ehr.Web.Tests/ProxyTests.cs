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
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

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
