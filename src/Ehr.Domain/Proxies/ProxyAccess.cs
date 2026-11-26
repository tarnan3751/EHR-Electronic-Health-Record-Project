using Ehr.Data;
using Ehr.Data.Proxies;
using Ehr.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Proxies;

// Giving and taking away proxy access (FR-19): one person's permission to use the portal for a patient. The patient
// does it for their own record, in the portal; a clinician with the patient on their panel records it for them. This
// class checks what was typed and sends the rows; who may do it, who gave it and when, how long it may last, and that
// only the patient allows sensitive items, are the database's (db/policies/30-rls.sql and 95-proxy-access.sql), which
// refuses whatever this lets through. It reads nothing: lists come from the PHI reader.
public sealed class ProxyAccess(EhrDbContext db, IEmailSender email)
{
    // How long a grant may be given for, in days, as the forms offer it. The database allows up to a year.
    public static readonly IReadOnlyList<(int Days, string Label)> Durations = [(30, "30 days"), (90, "90 days"), (180, "6 months"), (365, "1 year")];

    public static string ScopeLabel(string scope) =>
        scope == ProxyScope.RecordAndMessages ? "Record, results, appointments and messages" : "Record, results and appointments";

    // What is wrong with what was typed, by field: empty when it can be sent.
    public static Dictionary<string, string> Check(string? proxyEmail, string? scope, int days)
    {
        var errors = new Dictionary<string, string>();
        var address = proxyEmail?.Trim() ?? "";
        if (address.Length == 0)
        {
            errors["Email"] = "Enter the person's email address.";
        }
        // Only the characters a portal account's address may have (ASP.NET Core Identity's defaults for a user name):
        // an address with any other could be given access and then never accept it.
        else if (address.Length > ProxyGrantConfiguration.MaximumEmail || address.Count(c => c == '@') != 1
            || address.Any(c => !(char.IsAsciiLetterOrDigit(c) || "-._@+".Contains(c)))
            || address.StartsWith('@') || address.EndsWith('@'))
        {
            errors["Email"] = "Enter an email address, like name@example.com.";
        }

        if (!ProxyScope.All.Contains(scope))
        {
            errors["Scope"] = "Choose what they can see.";
        }

        if (!Durations.Any(d => d.Days == days))
        {
            errors["Days"] = "Choose how long the access lasts.";
        }

        return errors;
    }

    // Gives the person with that portal email access to the patient's record. Giving it again replaces what they had:
    // the earlier grant is revoked first, in the same transaction.
    public async Task<(GiveResult Result, Guid Grant)> GiveAsync(Guid patient, IEnumerable<Guid> replaces, string proxyEmail, string scope, bool includeSensitive, int days,
        CancellationToken cancellationToken)
    {
        const string savepoint = "proxy_grant";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var revoked = replaces.Select(Revoking).ToList();
        var grant = new ProxyGrant
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            ProxyEmail = proxyEmail.Trim().ToUpperInvariant(),
            Scope = scope,
            IncludeSensitive = includeSensitive,
            // An hour under the whole days, so a year's grant is inside the database's year when it arrives, even if
            // this server's clock runs a little ahead of the database's.
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(days).AddHours(-1),
        };
        try
        {
            // The earlier grant first: only one that isn't revoked may exist for a patient and a proxy.
            await db.SaveChangesAsync(cancellationToken);
            // As an object: the row is written, never read here (PhiReaderTests).
            db.Add((object)grant);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return (GiveResult.Given, grant.Id);
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException || e is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.UniqueViolation
                    or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.ProgramLimitExceeded,
            },
        })
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ((e as DbUpdateException)?.InnerException switch
            {
                PostgresException { SqlState: PostgresErrorCodes.ProgramLimitExceeded } => GiveResult.TooMany,
                // The trigger names this one refusal (95-proxy-access.sql); any other check is just refused.
                PostgresException { SqlState: PostgresErrorCodes.CheckViolation, ConstraintName: "proxy_grants_not_oneself" } => GiveResult.Yourself,
                _ => GiveResult.Refused,
            }, grant.Id);
        }
        finally
        {
            foreach (var entry in revoked)
            {
                entry.State = EntityState.Detached;
            }

            db.Entry((object)grant).State = EntityState.Detached;
        }
    }

    // Ends a grant now. False when it isn't the signed-in account's to end, or has already ended.
    public Task<bool> RevokeAsync(Guid grant, CancellationToken cancellationToken) => UpdateAsync(Revoking(grant), cancellationToken);

    // The patient lets the proxy see items marked sensitive, or stops. False when the database refused: only the
    // patient may, as themselves.
    public Task<bool> SetSensitiveAsync(Guid grant, bool include, CancellationToken cancellationToken)
    {
        // A stand-in for the row, with its key: only include_sensitive is sent.
        var entry = db.Attach((object)new ProxyGrant { Id = grant, ProxyEmail = "", Scope = "", IncludeSensitive = include });
        entry.Property(nameof(ProxyGrant.IncludeSensitive)).IsModified = true;
        return UpdateAsync(entry, cancellationToken);
    }

    // Tells the address that someone gave it access, and nothing else: no names, and the same whether or not a portal
    // account has that address. The link creates an account for someone who has none (ProxyInvitations): it works
    // once and expires. Someone who has an account signs in as usual.
    public Task NotifyAsync(string proxyEmail, string signInLink, string invitationLink, CancellationToken cancellationToken) =>
        email.SendAsync(proxyEmail.Trim(), "You have been given access to a record",
            "Someone gave you access to their record in the patient portal, or their clinic recorded it. For their privacy this email doesn't say who. "
                + $"If you already have a portal account with this email address, sign in at {signInLink} to see it. "
                + $"If you don't, create one at {invitationLink} (the link works once and expires in 7 days).",
            new EmailAction("Create your portal account", invitationLink), cancellationToken);

    // A stand-in for the row, with its key: only revoked_at is sent, and the database sets its value and who.
    Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry Revoking(Guid grant)
    {
        var entry = db.Attach((object)new ProxyGrant { Id = grant, ProxyEmail = "", Scope = "", RevokedAt = DateTimeOffset.UtcNow });
        entry.Property(nameof(ProxyGrant.RevokedAt)).IsModified = true;
        return entry;
    }

    async Task<bool> UpdateAsync(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, CancellationToken cancellationToken)
    {
        const string savepoint = "proxy_grant_update";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return true;
        }
        catch (Exception e) when (e is DbUpdateConcurrencyException
            || e is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.InsufficientPrivilege } })
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }
}

public enum GiveResult
{
    Given,

    // Not this account's to give.
    Refused,

    // The patient named their own email address.
    Yourself,

    // The patient already has as many grants as the database allows.
    TooMany,
}

// A grant as the lists show it. InForce is false once it is revoked or past its expiry.
public sealed record ProxyGrantEntry(
    Guid Id, string Email, string Scope, bool IncludeSensitive, bool ByClinic, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, bool InForce);

// A record the signed-in account may open as a proxy.
public sealed record ProxiedRecord(Guid GrantId, Guid PatientId, string PatientName, string Scope, bool IncludeSensitive, DateTimeOffset ExpiresAt);
