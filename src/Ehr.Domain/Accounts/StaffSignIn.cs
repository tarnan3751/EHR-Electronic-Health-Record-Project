using System.Security.Claims;
using System.Security.Cryptography;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Ehr.Domain.Accounts;

// A staff sign-in, step by step: the password; then whatever the account still owes (a new password for the
// bootstrap admin, an authenticator for a new account); then the second factor, a code or, where the account has one,
// a passkey (step 19, StaffPasskeys); then the Staff cookie. Between steps the account rides in the short-lived
// StaffPending cookie, never in a form field. Every outcome goes in the audit log. Identity's SignInManager isn't used:
// its two-factor flow assumes one kind of account per application.
public sealed class StaffSignIn(
    UserManager<StaffUser> users, IUserClaimsPrincipalFactory<StaffUser> principals, AuditRecorder audit, EhrDbContext db, StaffPasskeys passkeys)
{
    public const int RecoveryCodes = 8;

    // The first factor. A wrong password and an unknown address get the same answer, so the page never says which
    // addresses have accounts. Failures count towards the lockout. The password is checked first, with nothing held,
    // its hash being slow; then the account is held and read again (StaffUserLock) before anything is counted, reset or
    // recorded (step 19.2), so passwords sent at once for one account are counted one after another, never lost to the
    // account's concurrency stamp: the fifth wrong one locks it, and the rest are refused as locked. A right password
    // whose account changed between its check and the hold (a reissued or a new password) is answered as a wrong one,
    // counting nothing: the password checked is no longer the account's. The account stays held for the rest of the
    // request, for the desktop app's sign-in, which goes on to the code (DesktopSessions).
    public async Task<PasswordCheck> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return new(PasswordOutcome.Failed, null);
        }

        if (await LockedOutAsync(user, cancellationToken))
        {
            return new(PasswordOutcome.LockedOut, null);
        }

        var right = await users.CheckPasswordAsync(user, password);
        var (stamp, hash) = (user.SecurityStamp, user.PasswordHash);
        await HoldAccountAsync(user, cancellationToken);
        if (await LockedOutAsync(user, cancellationToken))
        {
            return new(PasswordOutcome.LockedOut, null);
        }

        if (!right)
        {
            await users.AccessFailedAsync(user);
            await RecordAsync(AuditActions.SignInFailed, user, "wrong password", cancellationToken);
            return new(await users.IsLockedOutAsync(user) ? PasswordOutcome.LockedOut : PasswordOutcome.Failed, null);
        }

        if (user.SecurityStamp != stamp || user.PasswordHash != hash)
        {
            return new(PasswordOutcome.Failed, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        return new(PasswordOutcome.Ok, user);
    }

    // Holds the account between steps, in the StaffPending cookie, with a value of its own for this password step, which
    // a passkey's ceremony is bound to (PendingAsync), and the account's security stamp as its password was accepted
    // (UnchangedAsync).
    public Task HoldAsync(HttpContext context, StaffUser user) =>
        context.SignInAsync(AccountSchemes.StaffPending, new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], AccountSchemes.StaffPending)),
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [PendingItem] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                [StampItem] = user.SecurityStamp,
            }));

    // The StaffPending ticket written again with the account's security stamp as a step of this sign-in has just changed
    // it (a new password, a new authenticator key), with its password step and its ten minutes as they were. The stamp
    // is the one this request wrote, never read again, so a change by anyone else in between still ends the sign-in.
    public async Task RestampAsync(HttpContext context, StaffUser user)
    {
        var pending = await context.AuthenticateAsync(AccountSchemes.StaffPending);
        pending.Properties!.Items[StampItem] = user.SecurityStamp;
        await context.SignInAsync(AccountSchemes.StaffPending, pending.Principal!, pending.Properties);
    }

    // This password step, as its StaffPending cookie has it, or null without one.
    public static async Task<string?> PendingAsync(HttpContext context)
    {
        var pending = await context.AuthenticateAsync(AccountSchemes.StaffPending);
        return pending.Succeeded && pending.Properties!.Items.TryGetValue(PendingItem, out var value) ? value : null;
    }

    const string PendingItem = "ehr:pending";

    const string StampItem = "ehr:stamp";

    // The account held between steps, or null when there is none, it has expired, or the account has changed since
    // (UnchangedAsync): the person starts over.
    public async Task<StaffUser?> HeldAsync(HttpContext context)
    {
        var pending = await context.AuthenticateAsync(AccountSchemes.StaffPending);
        var id = pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is not null && await users.FindByIdAsync(id) is { } user && await UnchangedAsync(context, user) ? user : null;
    }

    // Whether the account's security stamp is still the one its StaffPending ticket carries (step 19.1's final review). A
    // reissued password, a reset authenticator and a disabled account each change it, and end a sign-in begun before
    // them between its password and its second factor, as they end the account's sessions; a step completing the
    // sign-in asks again once it holds the account. A ticket written before the stamp was kept carries none, and the
    // person starts over.
    public async Task<bool> UnchangedAsync(HttpContext context, StaffUser user)
    {
        var pending = await context.AuthenticateAsync(AccountSchemes.StaffPending);
        return pending.Succeeded && pending.Properties!.Items.TryGetValue(StampItem, out var stamp)
            && stamp == await users.GetSecurityStampAsync(user);
    }

    // What the account still has to do before it is signed in.
    public async Task<SignInStep> NextStepAsync(StaffUser user)
    {
        if (user.MustChangePassword)
        {
            return SignInStep.ChangePassword;
        }

        return user.TwoFactorEnabled && await users.GetAuthenticatorKeyAsync(user) is not null
            ? SignInStep.EnterCode
            : SignInStep.EnrollAuthenticator;
    }

    // The new password a held account owes, for the bootstrap admin. Identity's rules apply (AddEhrAccounts).
    public async Task<IdentityResult> ChangePasswordAsync(StaffUser user, string newPassword, CancellationToken cancellationToken)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, newPassword);
        if (result.Succeeded)
        {
            user.MustChangePassword = false;
            await users.UpdateAsync(user);
            await RecordAsync(AuditActions.PasswordChanged, user, null, cancellationToken);
        }

        return result;
    }

    // The authenticator key the account is enrolling, with the URI authenticator apps scan or the key they take typed:
    // a fresh one when enrollment starts, the same one again after a mistyped code.
    public async Task<AuthenticatorSetup> EnrollmentAsync(StaffUser user, bool newKey)
    {
        if (newKey || await users.GetAuthenticatorKeyAsync(user) is null)
        {
            await users.ResetAuthenticatorKeyAsync(user);
        }

        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        var uri = $"otpauth://totp/EHR:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=EHR&digits=6&period=30";
        return new(key, uri);
    }

    // A code from the app proves it holds the key. The account then has its second factor, and recovery codes that
    // are shown once; the caller signs the account in, since it has just proved both factors.
    public async Task<IReadOnlyList<string>?> FinishEnrollmentAsync(StaffUser user, string code, CancellationToken cancellationToken)
    {
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code)))
        {
            return null;
        }

        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodes))!.ToList();
        await RecordAsync(AuditActions.MfaEnrolled, user, null, cancellationToken);
        return codes;
    }

    // The second factor: a code from the authenticator app, or one of the recovery codes when the app is lost. A wrong
    // code counts towards the lockout like a wrong password.
    public async Task<bool> VerifyCodeAsync(StaffUser user, string code, CancellationToken cancellationToken)
    {
        if (await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, Normalize(code)))
        {
            return true;
        }

        if (RecoveryCode(code) is { } recoveryCode && (await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode)).Succeeded)
        {
            await RecordAsync(AuditActions.RecoveryCodeUsed, user, null, cancellationToken);
            return true;
        }

        await users.AccessFailedAsync(user);
        await RecordAsync(AuditActions.SignInFailed, user, "wrong code", cancellationToken);
        return false;
    }

    // The second factor in place of the code: one of the account's passkeys, from the options StaffPasskeys gave this
    // password step (BeginSignInAsync). Its signature count is kept. Refused, it counts towards the lockout as a wrong code does, and
    // is recorded; null then. Otherwise the audit log's details for the sign-in, which name the passkey.
    public async Task<string?> VerifyPasskeyAsync(HttpContext context, StaffUser user, string? credentialJson, string? state, CancellationToken cancellationToken)
    {
        if (await passkeys.SignInAsync(context, user, credentialJson, state, await PendingAsync(context)) is { } used)
        {
            return used;
        }

        await users.AccessFailedAsync(user);
        await RecordAsync(AuditActions.SignInFailed, user, "passkey refused", cancellationToken);
        return null;
    }

    // The account held for the rest of the request (StaffUserLock) and read again, before its second factor is checked:
    // second factors racing on one account are then checked and counted one after another, never lost to the account's
    // concurrency stamp and rolled back (step 19's final review).
    public Task HoldAccountAsync(StaffUser user, CancellationToken cancellationToken) => StaffUserLock.AcquireAsync(db, user, cancellationToken);

    // An account locked out, by wrong passwords or wrong second factors, takes no second factor until the lockout ends,
    // as it takes no password (CheckPasswordAsync). Recorded when it is so.
    public async Task<bool> LockedOutAsync(StaffUser user, CancellationToken cancellationToken)
    {
        if (!await users.IsLockedOutAsync(user))
        {
            return false;
        }

        await RecordAsync(AuditActions.SignInFailed, user, "locked out", cancellationToken);
        return true;
    }

    // Signed in, with the second factor's details for the audit log: none for a code, the passkey for a passkey.
    public async Task CompleteAsync(HttpContext context, StaffUser user, CancellationToken cancellationToken, string? details = null)
    {
        await context.SignOutAsync(AccountSchemes.StaffPending);
        await context.SignInAsync(AccountSchemes.Staff, await principals.CreateAsync(user));
        await RecordAsync(AuditActions.SignIn, user, null, cancellationToken, details);
    }

    public async Task SignOutAsync(HttpContext context, Guid? userId, CancellationToken cancellationToken)
    {
        await context.SignOutAsync(AccountSchemes.Staff);
        if (userId is { } id)
        {
            audit.Record(AuditActions.SignOut, actorId: id);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // An authenticator app's codes are typed with the spaces or dashes apps show them with. Adding a passkey takes one
    // too (StaffPasskeys), and never a recovery code.
    internal static string Normalize(string code) => code.Replace(" ", "").Replace("-", "");

    // A recovery code in the form Identity makes and stores it, five letters or digits in upper case, a dash and five
    // more, however it was typed: as the enrolment page shows it, without its dash, in either case, with spaces round
    // it. Null for anything that can't be one. Before step 19.1 a recovery code went through Normalize, which took its
    // dash out, so none ever matched the one stored.
    static string? RecoveryCode(string code)
    {
        var plain = new string([.. code.Where(char.IsAsciiLetterOrDigit)]).ToUpperInvariant();
        return plain.Length == 10 ? $"{plain[..5]}-{plain[5..]}" : null;
    }

    async Task RecordAsync(string action, StaffUser user, string? reason, CancellationToken cancellationToken, string? details = null)
    {
        audit.Record(action, reason: reason, actorId: user.Id, details: details);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public enum PasswordOutcome
{
    Ok,

    Failed,

    LockedOut,
}

public sealed record PasswordCheck(PasswordOutcome Outcome, StaffUser? User);

public enum SignInStep
{
    ChangePassword,

    EnrollAuthenticator,

    EnterCode,
}

// The key as Identity stores it (base32) and the otpauth URI that carries it, the account's email and the issuer.
public sealed record AuthenticatorSetup(string Key, string Uri);
