using System.Net;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.ProxyTests;

namespace Ehr.Web.Tests.TestCases.UC18;

// Test Case ID: TC-UC18-08
// Purpose / scenario: What an invitation and a proxy-only account refuse. A link works once and not after it expires;
//   a mistyped password leaves it usable; an address that already has an account is told to sign in; and the account
//   it creates can't share anything, and is back to an empty "Whose record?" when the access is removed.
// Type: Functional (invalid input and exceptional condition)
// Requirements: FR-19, proposed FR-44, proposed FR-46
// Use case: UC 18, Give someone proxy access to a record
// Preconditions: Two patients with portal accounts. The first gives access to an address with no account, to a second
//   such address, and to the second patient's address.
// Inputs and system conditions: GET and POST /portal/invitation, signed out; POST /portal/access as the new account;
//   POST /portal/access?handler=Revoke as the patient; on the on-site app.
// Test data: A made-up code. Passwords that don't match. An invitation whose end date the test moves into the past.
// Steps:
//   1. Open /portal/invitation with a made-up code, and with none.
//   2. With the first link, post passwords that don't match; then post matching ones; then post the same link again.
//   3. Post the second link after its end date.
//   4. Post the link sent to the second patient's address.
//   5. As the new account, viewing the record, post the "Give someone access" form.
//   6. As the patient, remove the new account's access. As the new account, open "Results".
// Expected result: Step 1: "This invitation link isn't valid any more." and no form. Step 2: "The passwords don't
//   match." with the form still there and no account; then "Created:"; then "isn't valid any more", and still one
//   account. Step 3: "isn't valid any more" and no account. Step 4: "You already have a portal account with this email
//   address." and that patient's account is unchanged. Step 5: nothing is saved. Step 6: the new account lands on
//   "Whose record?" with "Access ended:" and "Nobody is sharing a record with you at the moment."
// Creator: Tahseen Arnan
public class TC_UC18_08_InvitationRefused(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_invitation_works_once_until_it_expires_and_its_account_shares_nothing()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Inviter");
        var existing = await PersonAsync(apps, "Synthetic", "Already");
        var first = $"synthetic-first-{Guid.NewGuid():N}@example.com";
        var second = $"synthetic-second-{Guid.NewGuid():N}@example.com";
        var anonymous = Client(apps.OnPrem, new CookieContainer());

        foreach (var path in new[] { "/portal/invitation?code=0123456789ABCDEF0123456789ABCDEF01234567", "/portal/invitation" })
        {
            var page = await anonymous.GetStringAsync(path, Cancel);
            Assert.Contains("This invitation link isn't valid any more.", Words(page));
            Assert.DoesNotContain("name=\"Input.Password\"", page);
        }

        await GiveAsync(patient, first);
        var link = InvitationPath(apps, first);
        var mismatch = await AcceptAsync(apps, link, "Synthetic First", confirm: "a-different-password-entirely");
        Assert.Contains("The passwords don&#x27;t match.", mismatch);
        Assert.Contains("name=\"Input.Password\"", mismatch);
        Assert.Empty(await Accounts(first));
        Assert.Contains("Created:", await AcceptAsync(apps, link, "Synthetic First"));
        Assert.Contains("This invitation link isn't valid any more.", Words(await AcceptAsync(apps, link, "Synthetic Again")));
        Assert.Equal(["Synthetic First"], await Accounts(first));

        await GiveAsync(patient, second);
        var expired = InvitationPath(apps, second);
        // The used code posted again from a form that was still open: refused on the server, not just by the page.
        var usedCode = System.Web.HttpUtility.ParseQueryString(link[(link.IndexOf('?') + 1)..])["code"]!;
        using var again = await anonymous.PostAsync("/portal/invitation", Form(("__RequestVerificationToken", Token(await anonymous.GetStringAsync(expired, Cancel))),
            ("Input.Code", usedCode), ("Input.Name", "Synthetic Twice"), ("Input.Password", Password), ("Input.Confirm", Password)), Cancel);
        Assert.Contains("This invitation link isn't valid any more.", Words(await again.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal(["Synthetic First"], await Accounts(first));
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var age = new Npgsql.NpgsqlCommand("UPDATE ehr.proxy_invitations SET expires_at = now() - interval '1 minute' WHERE email = @email", db);
            age.Parameters.AddWithValue("email", second);
            Assert.Equal(1, await age.ExecuteNonQueryAsync(Cancel));
        }

        Assert.Contains("This invitation link isn't valid any more.", Words(await anonymous.GetStringAsync(expired, Cancel)));
        Assert.Contains("This invitation link isn't valid any more.", Words(await AcceptAsync(apps, expired, "Synthetic Late")));
        Assert.Empty(await Accounts(second));

        await GiveAsync(patient, existing.Email);
        Assert.Contains("You already have a portal account with this email address.", Words(await AcceptAsync(apps, InvitationPath(apps, existing.Email), "Synthetic Impostor")));
        Assert.Equal([""], await Accounts(existing.Email));
        Assert.Equal("/portal/choose", (await SignInAsync(apps, existing.Email)).Path);

        var session = await SignInAsync(apps, first);
        var grants = await GrantsAsync(apps, patient.Patient);
        var grant = Guid.Parse((await QueryAsync(apps,
            $"SELECT id::text FROM ehr.proxy_grants WHERE patient_id = @patient AND lower(proxy_email) = '{first}'", patient.Patient)).Single());
        using var chosen = await PostFormAsync(session.Client, "/portal/acting", "/portal/choose", ("grantId", grant));
        Assert.Equal("/portal", PathOf(chosen));
        using var passedOn = await PostFormAsync(session.Client, "/portal/access", "/portal/access", ("Email", second), ("Scope", "record"), ("Days", 30));
        Assert.Equal("/portal/access", PathOf(passedOn));
        Assert.Equal(grants, await GrantsAsync(apps, patient.Patient));

        using var removed = await PostFormAsync(patient.Client, "/portal/access?handler=Revoke", "/portal/access", ("grantId", grant));
        Assert.Contains("Removed:", await ReadAsync(removed));
        using var after = await session.Client.GetAsync("/portal/results", Cancel);
        Assert.Equal("/portal/choose", PathOf(after));
        var ended = await ReadAsync(after);
        Assert.Contains("Access ended: you can no longer view that record.", Words(ended));
        Assert.Contains("Nobody is sharing a record with you at the moment.", ended);
        Assert.DoesNotContain("Synthetic Inviter", ended);
    }

    // The display names of the portal accounts with that address: none, or one.
    async Task<List<string>> Accounts(string email)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new Npgsql.NpgsqlCommand("SELECT coalesce(display_name, '') FROM ehr.patient_users WHERE normalized_email = upper(@email)", db);
        command.Parameters.AddWithValue("email", email);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
