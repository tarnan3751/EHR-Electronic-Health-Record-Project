using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using Ehr.Data;
using Ehr.Domain.QuickTexts;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Api.Sync;

// The desktop app's sync for quick texts. Both servers answer it: the cloud app serves pulls from its replica and
// forwards pushes to the on-prem app (Forwarding/). The JSON is in QuickTextSyncContract.cs.
//
// Pull, GET /api/sync/quick-texts?since=<watermark>: the phrases changed since the watermark, and the next
// watermark. since=0, for the first pull, returns every phrase. A phrase can come again in a later pull; applying
// it twice is harmless.
//
// Push, POST /api/sync/quick-texts: up to MaxOperations changes. Each comes back applied, duplicate, conflict or
// invalid, and each conflict carries what's saved now, for the person to choose (QuickTextSaver).
public static class QuickTextSync
{
    public const int MaxOperations = 100;

    public static RouteGroupBuilder MapQuickTextSync(this IEndpointRouteBuilder endpoints)
    {
        var sync = endpoints.MapGroup("/api/sync/quick-texts");
        sync.MapGet("/", PullAsync);
        sync.MapPost("/", PushAsync);
        return sync;
    }

    static async Task<IResult> PullAsync(string? since, EhrDbContext db, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(since ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out var from))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["since"] = ["Use the watermark from the last pull, or 0 for everything."],
            });
        }

        // One snapshot for the watermark and the phrases, so whatever this pull can't see is in the next one.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var watermark = await ChangeLog.WatermarkAsync(db, cancellationToken);

        // A watermark ahead of this server's means the client last pulled from a primary this replica hasn't caught
        // up with, or the database was restored to an earlier point. Starting over can't skip anything.
        var changed = from == 0 || from > watermark ? db.QuickTexts : ChangeLog.QuickTextsChanged(db, from, watermark);
        var quickTexts = await changed.AsNoTracking().OrderBy(q => q.Id).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(new PullResponse(
            watermark.ToString(CultureInfo.InvariantCulture), quickTexts.Select(SyncedQuickText.From).ToList()));
    }

    static async Task<IResult> PushAsync(
        PushRequest request, EhrDbContext db, QuickTextSaver saver, CancellationToken cancellationToken)
    {
        if (request.Operations is not { Count: > 0 and <= MaxOperations } operations)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["operations"] = [$"Send 1 to {MaxOperations} operations."],
            });
        }

        // One transaction for the batch; each operation is its own savepoint inside it (QuickTextSaver).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var results = new List<PushResult>(operations.Count);
        foreach (var operation in operations)
        {
            results.Add(await ApplyAsync(operation, db, saver, cancellationToken));
        }

        await transaction.CommitAsync(CancellationToken.None);
        return Results.Ok(new PushResponse(results));
    }

    static async Task<PushResult> ApplyAsync(
        PushOperation operation, EhrDbContext db, QuickTextSaver saver, CancellationToken cancellationToken)
    {
        if (operation.Key == Guid.Empty || operation.Id == Guid.Empty)
        {
            return Invalid(operation, "key", "Give every operation a key and a phrase ID.");
        }

        if (await db.SyncOperations.AsNoTracking()
                .SingleOrDefaultAsync(o => o.IdempotencyKey == operation.Key, cancellationToken) is { } applied)
        {
            return new(operation.Key, PushOutcome.Duplicate, await FindAsync(db, applied.RowId, cancellationToken), null);
        }

        var input = new QuickTextInput { Shortcut = operation.Shortcut, Body = operation.Body };
        var problems = new List<ValidationResult>();
        if (!Validator.TryValidateObject(input, new ValidationContext(input), problems, validateAllProperties: true))
        {
            var errors = problems
                .SelectMany(problem => problem.MemberNames, (problem, member) => (Member: member, problem.ErrorMessage))
                .GroupBy(error => JsonName(error.Member), error => error.ErrorMessage ?? "Not valid.")
                .ToDictionary(group => group.Key, group => group.ToArray());
            return new(operation.Key, PushOutcome.Invalid, null, errors);
        }

        var saved = operation.BaseVersion is { } baseVersion
            ? await saver.UpdateAsync(operation.Id, baseVersion, input, operation.Key, cancellationToken)
            : await saver.AddAsync(operation.Id, input, operation.Key, cancellationToken);

        return saved.Outcome switch
        {
            QuickTextSaveOutcome.Saved => Result(PushOutcome.Applied),
            QuickTextSaveOutcome.ChangedSince or QuickTextSaveOutcome.ShortcutTaken => Result(PushOutcome.Conflict),
            QuickTextSaveOutcome.AlreadyApplied => Result(PushOutcome.Duplicate),
            _ => Invalid(operation, "id", "No phrase has this ID."),
        };

        PushResult Result(PushOutcome outcome) =>
            new(operation.Key, outcome, saved.QuickText is { } quickText ? SyncedQuickText.From(quickText) : null, null);
    }

    static async Task<SyncedQuickText?> FindAsync(EhrDbContext db, Guid id, CancellationToken cancellationToken) =>
        await db.QuickTexts.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, cancellationToken) is { } quickText
            ? SyncedQuickText.From(quickText)
            : null;

    static PushResult Invalid(PushOperation operation, string field, string message) =>
        new(operation.Key, PushOutcome.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });

    static string JsonName(string member) => char.ToLowerInvariant(member[0]) + member[1..];
}
