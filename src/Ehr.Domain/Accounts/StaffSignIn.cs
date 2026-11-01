using System.Security.Claims;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Ehr.Domain.Accounts;

// A staff sign-in, step by step: the password; then whatever the account still owes (a new password for the
// bootstrap admin, an authenticator for a new account); then the second factor; then the Staff cookie. Between steps
// the account rides in the short-lived StaffPending cookie, never in a form field. Every outcome goes in the audit
// log. Identity's SignInManager isn't used: its two-factor flow assumes one kind of account per application.
public sealed class StaffSignIn(
    UserManager<StaffUser> users, IUserClaimsPrincipalFactory<StaffUser> principals, AuditRecorder audit, EhrDbContext db)
{
    public const int RecoveryCodes = 8;

    // The first factor. A wrong password and an unknown address get the same answer, so the page never says which
    // addresses have accounts. Failures count towards the lockout.
    public async Task<PasswordCheck> CheckPasswordAsync(string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return new(PasswordOutcome.Failed, null);
        }

        if (await users.IsLockedOutAsync(user))
        {
            await RecordAsync(AuditActions.SignInFailed, user, "locked out", cancellationToken);
            return new(PasswordOutcome.LockedOut, null);
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await RecordAsync(AuditActions.SignInFailed, user, "wrong password", cancellationToken);
            return new(await users.IsLockedOutAsync(user) ? PasswordOutcome.LockedOut : PasswordOutcome.Failed, null);
        }

        await users.ResetAccessFailedCountAsync(user);
        return new(PasswordOutcome.Ok, user);
    }

    // Holds the account between steps, in the StaffPending cookie.
    public Task HoldAsync(HttpContext context, StaffUser user) =>
        context.SignInAsync(AccountSchemes.StaffPending, new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], AccountSchemes.StaffPending)));

    // The account held between steps, or null when there is none or it has expired: the person starts over.
    public async Task<StaffUser?> HeldAsync(HttpContext context)
    {
        var pending = await context.AuthenticateAsync(AccountSchemes.StaffPending);
        var id = pending.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        return id is null ? null : await users.FindByIdAsync(id);
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
        var normalized = Normalize(code);
        if (await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized))
        {
            return true;
        }

        if ((await users.RedeemTwoFactorRecoveryCodeAsync(user, normalized)).Succeeded)
        {
            await RecordAsync(AuditActions.RecoveryCodeUsed, user, null, cancellationToken);
            return true;
        }

        await users.AccessFailedAsync(user);
        await RecordAsync(AuditActions.SignInFailed, user, "wrong code", cancellationToken);
        return false;
    }

    public async Task CompleteAsync(HttpContext context, StaffUser user, CancellationToken cancellationToken)
    {
        await context.SignOutAsync(AccountSchemes.StaffPending);
        await context.SignInAsync(AccountSchemes.Staff, await principals.CreateAsync(user));
        await RecordAsync(AuditActions.SignIn, user, null, cancellationToken);
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

    // Codes are typed with the spaces or dashes apps show them with.
    static string Normalize(string code) => code.Replace(" ", "").Replace("-", "");

    async Task RecordAsync(string action, StaffUser user, string? reason, CancellationToken cancellationToken)
    {
        audit.Record(action, reason: reason, actorId: user.Id);
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
