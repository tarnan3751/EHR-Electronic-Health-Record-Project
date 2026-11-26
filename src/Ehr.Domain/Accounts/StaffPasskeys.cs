using System.Security.Cryptography;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// A staff member's passkeys (the expansion plan's step 19, UC 11): a second factor in place of the authenticator code,
// after the password (StaffSignIn), never in place of the password, and never instead of the authenticator app, which
// every staff account keeps. Added only while signed in, after a current code from the authenticator app; at most
// MostPerAccount, each named; removed by their owner, or all of them by an administrator's authenticator reset
// (StaffAccounts). Identity's own handler makes and checks each ceremony (IPasskeyHandler), and the person must be
// verified by the device every time (AddEhrAccounts). Between the options the page is given and the credential it sends
// back, the handler's state travels in the page, protected by Data Protection, for a few minutes, bound to its purpose,
// to the account and to this sign-in, or for a sign-in to this password step (StaffSignIn.PendingAsync): a state from
// another account's page, or from another sign-in or password step of the same account, is refused. An add's state also
// carries the account's security stamp, and an administrator's authenticator reset in between refuses it (step 19.1).
// Every add, refusal and removal is an audit event that names the passkey by a short hash, never its name. Each change
// holds the account's row first (StaffUserLock), so a locked-out account is refused before a code is checked, every
// wrong code is counted however requests race, and two adds finishing at once can't both pass the limit; and an add or a
// removal Identity fails to save throws, so the request's transaction is rolled back with nothing half-saved, and a
// passkey's row and its audit event always commit together.
public sealed class StaffPasskeys(
    UserManager<StaffUser> users, IPasskeyHandler<StaffUser> handler, IOptions<IdentityPasskeyOptions> options,
    IDataProtectionProvider dataProtection, AuditRecorder audit, EhrDbContext db, ILogger<StaffPasskeys> logger)
{
    public const int MostPerAccount = 10;

    public const int LongestName = 50;

    const string Adding = "add";

    const string SigningIn = "sign-in";

    readonly ITimeLimitedDataProtector protector = dataProtection.CreateProtector("Ehr.Accounts.Passkeys").ToTimeLimitedDataProtector();

    // Oldest first.
    public async Task<IReadOnlyList<UserPasskeyInfo>> ListAsync(StaffUser user) =>
        [.. (await users.GetPasskeysAsync(user)).OrderBy(passkey => passkey.CreatedAt)];

    // The options for the browser to make a passkey with, and the state to send back with it; or what is wrong. The
    // name, and a current code from the authenticator app (never a recovery code), come first. A wrong code counts
    // towards the lockout, as at sign-in, and is recorded.
    public async Task<PasskeyCeremony> BeginAddAsync(HttpContext context, StaffUser user, string? name, string? code, CancellationToken cancellationToken)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > LongestName)
        {
            return PasskeyCeremony.Refused("Name", $"Not saved: name the passkey, in {LongestName} characters or fewer, such as This Mac.");
        }

        if (await LockedOutAsync(user, cancellationToken))
        {
            return PasskeyCeremony.Refused(null, Locked);
        }

        if ((await users.GetPasskeysAsync(user)).Count >= MostPerAccount)
        {
            return PasskeyCeremony.Refused(null, TooMany);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return PasskeyCeremony.Refused("Code", "Not saved: enter the code from your authenticator app.");
        }

        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, StaffSignIn.Normalize(code)))
        {
            await users.AccessFailedAsync(user);
            audit.Record(AuditActions.PasskeyRefused, tableName: StaffTable, rowId: user.Id, reason: "wrong code");
            await db.SaveChangesAsync(cancellationToken);
            return PasskeyCeremony.Refused("Code", "Not saved: that code did not match. Enter the current code from your authenticator app.");
        }

        if (await SignedInAsync(context) is not { } binding)
        {
            return PasskeyCeremony.Refused(null, StartAgain);
        }

        var made = await handler.MakeCreationOptionsAsync(
            new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.Email!, DisplayName = user.DisplayName }, context);
        return PasskeyCeremony.Ready(made.CreationOptionsJson,
            Protect(new(Adding, user.Id, binding, made.AttestationState!, name, await users.GetSecurityStampAsync(user))));
    }

    // The passkey the browser made, added to the account; or why not. Refused if the account's security stamp has
    // changed since its code was checked (step 19.1): an administrator's authenticator reset changes it, and so does an
    // authenticator enrolled after one, so an add begun before a reset never outlives it. Nothing about the passkey is
    // checked then, and the refusal doesn't count towards the lockout.
    public async Task<string?> FinishAddAsync(HttpContext context, StaffUser user, string? credentialJson, string? state, CancellationToken cancellationToken)
    {
        if (Unprotect(state, Adding, user.Id, await SignedInAsync(context)) is not { } ceremony || string.IsNullOrEmpty(credentialJson))
        {
            return StartAgain;
        }

        if (await LockedOutAsync(user, cancellationToken))
        {
            return Locked;
        }

        if (ceremony.Stamp != await users.GetSecurityStampAsync(user))
        {
            audit.Record(AuditActions.PasskeyRefused, tableName: StaffTable, rowId: user.Id, reason: "account changed");
            await db.SaveChangesAsync(cancellationToken);
            return "Not saved: your account's sign-in settings changed while this passkey was being added. Sign in again, then add it.";
        }

        var made = await handler.PerformAttestationAsync(
            new PasskeyAttestationContext { HttpContext = context, CredentialJson = credentialJson, AttestationState = ceremony.State });
        if (!made.Succeeded)
        {
            logger.LogWarning("A passkey wasn't accepted as it was added: {Failure}", made.Failure?.Message);
            audit.Record(AuditActions.PasskeyRefused, tableName: StaffTable, rowId: user.Id, reason: "not accepted");
            await db.SaveChangesAsync(cancellationToken);
            return "Not saved: the passkey wasn't accepted. A passkey has to ask for your PIN, fingerprint or face. Try again.";
        }

        if ((await users.GetPasskeysAsync(user)).Count >= MostPerAccount)
        {
            return TooMany;
        }

        made.Passkey.Name = ceremony.Name;
        Saved(await users.AddOrUpdatePasskeyAsync(user, made.Passkey));
        audit.Record(AuditActions.PasskeyAdded, tableName: StaffTable, rowId: user.Id, details: Details(made.Passkey.CredentialId));
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // One of the account's own passkeys removed, by its owner or by an administrator's reset (StaffAccounts), who is
    // recorded as doing it. False when the account has no such passkey. Sessions already signed in with it go on: an
    // administrator's authenticator reset ends those.
    public async Task<bool> RemoveAsync(StaffUser user, byte[] credentialId, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        if (await users.GetPasskeyAsync(user, credentialId) is null)
        {
            return false;
        }

        Saved(await users.RemovePasskeyAsync(user, credentialId));
        audit.Record(AuditActions.PasskeyRemoved, tableName: StaffTable, rowId: user.Id, details: Details(credentialId));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // The options for the browser to use one of the account's passkeys with, for this password step, and the state to
    // send back with it; refused without a password step. Only the account's own passkeys are allowed, and the state
    // names the account, so a passkey of anyone else's is refused (SignInAsync).
    public async Task<PasskeyCeremony> BeginSignInAsync(HttpContext context, StaffUser user, string? pending)
    {
        if (pending is null)
        {
            return PasskeyCeremony.Refused(null, "Not signed in: this sign-in took too long. Start again with your password.");
        }

        var made = await handler.MakeRequestOptionsAsync(user, context);
        return PasskeyCeremony.Ready(made.RequestOptionsJson, Protect(new(SigningIn, user.Id, pending, made.AssertionState!, null, null)));
    }

    // The passkey the browser used, checked for this account and password step: its signature, the person verified,
    // the origin a staff site's, the count gone up. Its new count is kept. The audit log's details for the sign-in, or
    // null when it is refused, which the caller counts and records (StaffSignIn.VerifyPasskeyAsync).
    public async Task<string?> SignInAsync(HttpContext context, StaffUser user, string? credentialJson, string? state, string? pending)
    {
        if (Unprotect(state, SigningIn, user.Id, pending) is not { } ceremony || string.IsNullOrEmpty(credentialJson))
        {
            return null;
        }

        var used = await handler.PerformAssertionAsync(
            new PasskeyAssertionContext { HttpContext = context, CredentialJson = credentialJson, AssertionState = ceremony.State });
        if (!used.Succeeded)
        {
            logger.LogWarning("A passkey was refused at sign-in: {Failure}", used.Failure?.Message);
            return null;
        }

        Saved(await users.AddOrUpdatePasskeyAsync(user, used.Passkey));
        return Details(used.Passkey.CredentialId);
    }

    // A passkey as the audit log names it: the start of a hash of its credential's ID, the same at every event.
    public static string Reference(byte[] credentialId) => Convert.ToHexStringLower(SHA256.HashData(credentialId))[..12];

    static string Details(byte[] credentialId) => JsonSerializer.Serialize(new { passkey = Reference(credentialId) });

    const string StaffTable = "staff_users";

    const string Locked = "Not saved: this account is locked after too many failed attempts. Try again in 15 minutes.";

    // The account held for the rest of the request (StaffUserLock), then refused, and the refusal recorded, if it is
    // locked out: by wrong passwords or codes, at sign-in or here, or disabled.
    async Task<bool> LockedOutAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        if (!await users.IsLockedOutAsync(user))
        {
            return false;
        }

        audit.Record(AuditActions.PasskeyRefused, tableName: StaffTable, rowId: user.Id, reason: "locked out");
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Identity's store saves a passkey's row before it updates the account, so a failure after it would leave the row
    // saved without its audit event: it throws instead, and the request's transaction is rolled back.
    static void Saved(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"A passkey change wasn't saved: {result.Errors.First().Code}.");
        }
    }

    const string TooMany = "Not saved: an account can have at most 10 passkeys. Remove one first.";

    const string StartAgain = "Not saved: the passkey took too long, or this page was open too long. Try again.";

    // This staff sign-in, as its cookie's ticket has it: when it began (SessionTimes), kept by every renewal and
    // different for every sign-in. Null without one.
    static async Task<string?> SignedInAsync(HttpContext context)
    {
        var staff = await context.AuthenticateAsync(AccountSchemes.Staff);
        return staff.Succeeded && SessionTimes.SignedIn(staff.Properties!) is { } at ? at.UtcDateTime.ToString("O") : null;
    }

    string Protect(Ceremony ceremony) =>
        protector.Protect(JsonSerializer.Serialize(ceremony), options.Value.AuthenticatorTimeout + TimeSpan.FromMinutes(1));

    // The ceremony the state names, if it is for this purpose, account and binding and hasn't expired; otherwise null.
    Ceremony? Unprotect(string? state, string purpose, Guid account, string? binding)
    {
        if (string.IsNullOrEmpty(state) || binding is null)
        {
            return null;
        }

        try
        {
            var ceremony = JsonSerializer.Deserialize<Ceremony>(protector.Unprotect(state));
            return ceremony is not null && ceremony.Purpose == purpose && ceremony.Account == account && ceremony.Binding == binding
                ? ceremony
                : null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    // What the page carries between the options and the credential: what it is for, whose, which sign-in, Identity's
    // own state (the challenge), and, for an add, the name being given and the account's security stamp when its code
    // was checked (none for a sign-in, whose state its purpose keeps from an add).
    sealed record Ceremony(string Purpose, Guid Account, string Binding, string State, string? Name, string? Stamp);
}

// The options for the browser and the state to send back with its credential, or the field that is wrong and why, as
// a notice ("Not saved: ...").
public sealed record PasskeyCeremony(string? Options, string? State, string? Field, string? Problem)
{
    public static PasskeyCeremony Ready(string options, string state) => new(options, state, null, null);

    public static PasskeyCeremony Refused(string? field, string problem) => new(null, null, field, problem);
}
