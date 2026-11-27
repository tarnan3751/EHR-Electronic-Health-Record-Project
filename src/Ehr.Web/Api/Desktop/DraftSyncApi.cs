using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Web.Api.Desktop;

// Both routes belong to the Desktop bearer policy and are POSTs so the cloud forwards them to the primary.
static class DraftSyncApi
{
    public static void MapDraftSync(this RouteGroupBuilder desktop)
    {
        desktop.MapPost("/drafts/pull", PullAsync);
        desktop.MapPost("/drafts/push", PushAsync);
    }

    public sealed record PullRequest(string Since, Guid[] Patients);
    public sealed record PushRequest(Guid Key, Guid Id, Guid PatientId, long? BaseVersion, string? Body);
    // Withheld (step 10): the draft was signed as a sensitive note, which is never kept offline. Its text is not sent
    // (Body is empty), and a desktop that has an earlier copy of the text removes it.
    public sealed record Draft(Guid Id, Guid PatientId, string Body, long Version, bool Withheld = false);
    public sealed record PushResult(string Outcome, Draft? Current = null, string? Problem = null);

    static Guid? Identify(UserContext user, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var owner))
        {
            return null;
        }

        user.UserId = owner;
        return owner;
    }

    static async Task<IResult> PullAsync(PullRequest request, EhrDbContext db, PhiReader phi, UserContext user,
        HttpContext context, CancellationToken cancellationToken)
    {
        if (Identify(user, context) is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!ulong.TryParse(request.Since, NumberStyles.None, CultureInfo.InvariantCulture, out var since)
            || request.Patients is not { Length: <= 25 })
        {
            return Results.BadRequest();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var until = await ChangeLog.WatermarkAsync(db, cancellationToken);
        var drafts = await phi.ChangedDraftsAsync(request.Patients, since > until ? 0 : since, until, cancellationToken);
        var withheld = await phi.SensitiveSignedDraftsAsync([.. drafts.Select(d => d.Id)], cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(new { Watermark = until.ToString(CultureInfo.InvariantCulture), Drafts = drafts.Select(d => Describe(d, withheld.Contains(d.Id))) });
    }

    static async Task<IResult> PushAsync(PushRequest request, EhrDbContext db, PhiReader phi, UserContext user,
        HttpContext context, CancellationToken cancellationToken)
    {
        if (Identify(user, context) is not { } owner)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (request.Id == Guid.Empty || request.Key == Guid.Empty || request.PatientId == Guid.Empty
            || request.BaseVersion is <= 0 || string.IsNullOrWhiteSpace(request.Body)
            || request.Body.Contains('\0') || Encoding.UTF8.GetByteCount(request.Body) > 65_536)
        {
            return Results.Ok(new PushResult("invalid", Problem: "Enter a draft of up to 64 KiB."));
        }

        // Scope receipts to the clinician, draft and operation, independently of the Quick text keys.
        var receipt = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"clinical-draft:{owner:N}:{request.Id:N}:{request.Key:N}")).AsSpan(0, 16));
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await phi.IsAssignedAsync(request.PatientId, cancellationToken))
        {
            return Results.Ok(new PushResult("invalid", Problem: "This patient is no longer one of your patients."));
        }

        var current = await phi.ReadDraftAsync(request.Id, request.PatientId, cancellationToken);
        if (await db.SyncOperations.AnyAsync(o => o.IdempotencyKey == receipt && o.RowId == request.Id, cancellationToken)
            && current is not null)
        {
            var signedSensitive = await phi.SensitiveSignedDraftsAsync([current.Id], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new PushResult("duplicate", Describe(current, signedSensitive.Count > 0)));
        }

        // An already-applied operation keeps UC 8's duplicate outcome even after finalization. It performs no edit.
        if (current is not null && await phi.SignedDraftAsync(current.Id, request.PatientId, cancellationToken) is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new PushResult("invalid", Problem: "This note is signed and cannot be changed."));
        }

        if (current?.Version != request.BaseVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(current is null
                ? new PushResult("invalid", Problem: "This draft is no longer available on the server.")
                : new PushResult("conflict", Describe(current)));
        }

        if (current is null)
        {
            current = new ClinicalDraft
            {
                Id = request.Id,
                PatientId = request.PatientId,
                ClinicianId = owner,
                Body = request.Body,
                Version = 1,
            };
            db.AddRange(current);
        }
        else
        {
            current.Body = request.Body;
            current.Version++;
        }

        db.SyncOperations.Add(new SyncOperation { IdempotencyKey = receipt, RowId = request.Id, AppliedAt = DateTimeOffset.UtcNow });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new PushResult("applied", Describe(current)));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another writer committed after our read. The next round reads its committed version.
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation })
        {
            // Simultaneous creation, signing, or an ID already in use. Give no details about another owner's row.
            // The client retries once per round and does not lose its draft.
            return Results.StatusCode(StatusCodes.Status409Conflict);
        }
    }

    static Draft Describe(ClinicalDraft draft, bool withheld = false) =>
        new(draft.Id, draft.PatientId, withheld ? "" : draft.Body, draft.Version, withheld);
}
