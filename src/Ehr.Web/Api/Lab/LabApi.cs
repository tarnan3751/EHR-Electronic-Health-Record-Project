using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Lab;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Ehr.Web.Api.Lab;

// The lab's door (the expansion plan's step 12, UC 20): how a lab's system, the simulated lab (src/Ehr.Lab.Simulator)
// or a real one, receives the lab orders waiting for results and files their results. On the on-prem app only, and
// only when it has a key (Program.cs). The shapes are LabApiContract.cs.
//
//   GET  /api/lab/orders?since={watermark}   the lab orders signed since the lab last asked that still wait for
//                                           results, and where to ask from next time (0, or no since, to start over)
//   POST /api/lab/orders/{id}/results       one order's results, all or none; 204 when filed, or when the same was
//                                           filed before
//
// Who: the lab, by the key in KeyHeader, compared in fixed time with Ehr:Lab:Key, before anything is read; and nobody
// else's sign-in with it (a desktop token alongside the key is refused too). No account stands behind a lab, so the
// database checks again: it is given the key's hash, finds it among what the migration job stored, and lets nobody
// signed in through (db/policies/76-lab.sql). The cloud app maps none of this, and drops KeyHeader from every request it
// forwards, so a lab's key is no use from the internet. The whole of /api/lab is rate-limited as one (AccountRateLimits).
//
// What a lab gets is only what it needs, and every order handed over is recorded by the database as it is read, with
// the lab as the source (PhiReader.LabOrdersAsync); what it files goes through the same rule a clinician's filing does,
// and is audited the same way, as the lab's. Each request is one transaction. Every answer is no-store, and every
// refusal is a fixed word and sentence, with nothing from the request or a patient's record; the log says what was
// refused and never the key, an order or a patient.
public static class LabApi
{
    public const string Path = "/api/lab";

    // Its own header, not a bearer token: the API paths' authentication (DesktopTokenHandler) would look a bearer token
    // up as a desktop session on every request, and log that it isn't one.
    public const string KeyHeader = "X-Lab-Key";

    // Far more than the most an order's results take.
    public const long MaxBodySize = 16 * 1024;

    public const int MaxResults = 50;

    public static RouteGroupBuilder MapLabApi(this IEndpointRouteBuilder endpoints, LabApiKey key)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        var keyHash = Convert.ToHexStringLower(expected);
        // No staff or patient account calls these: the key is the credential, checked here and by the database.
        var group = endpoints.MapGroup(Path).AllowAnonymous();
        group.MapGet("/orders", (HttpContext context, PhiReader reader, EhrDbContext db, CancellationToken cancellationToken) =>
            Admitted(context, expected) is { } refused ? Task.FromResult(refused) : HandOverAsync(context, reader, db, keyHash, cancellationToken));
        group.MapPost("/orders/{id:guid}/results", (Guid id, HttpContext context, LabResults results, EhrDbContext db, CancellationToken cancellationToken) =>
                Admitted(context, expected) is { } refused ? Task.FromResult(refused) : FileAsync(id, context, results, db, keyHash, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodySize));
        return group;
    }

    // Null when the request comes from the lab: its key, and no account signed in. Every answer is no-store from here.
    static IResult? Admitted(HttpContext context, byte[] expected)
    {
        context.Response.Headers.CacheControl = "no-store";
        var presented = context.Request.Headers[KeyHeader];
        var signedIn = context.User.Identity?.IsAuthenticated == true;
        if (!signedIn && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString())), expected))
        {
            return null;
        }

        Log(context).LogWarning(
            "A request to the lab's door from {Address} was refused: {Why}.", context.Connection.RemoteIpAddress,
            presented.Count == 0 ? "it had no key" : signedIn ? "an account's sign-in came with it" : "its key is wrong");
        return Refused(StatusCodes.Status401Unauthorized, "unauthorized", $"Send the lab's key in {KeyHeader}, and no account's sign-in.");
    }

    static async Task<IResult> HandOverAsync(HttpContext context, PhiReader reader, EhrDbContext db, string keyHash, CancellationToken cancellationToken)
    {
        if (!TrySince(context.Request.Query, out var since))
        {
            return Refused(StatusCodes.Status400BadRequest, "malformed",
                "Give since as the watermark of the last answer, or leave it out to start from the beginning; nothing else.");
        }

        LabHandOver handOver;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            handOver = await reader.LabOrdersAsync(keyHash, since, cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return NotSetUp(context);
        }

        // Through the contract and back, so the answer holds what it names and nothing else.
        var orders = JsonSerializer.Deserialize<List<LabOrder>>(handOver.Orders, LabJson.Options)!;
        return Results.Json(new LabOrders(handOver.Watermark.ToString(CultureInfo.InvariantCulture), handOver.More, orders), LabJson.Options);
    }

    static async Task<IResult> FileAsync(Guid order, HttpContext context, LabResults results, EhrDbContext db, string keyHash, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType())
        {
            return Refused(StatusCodes.Status415UnsupportedMediaType, "unsupported", "Send the results as JSON (application/json).");
        }

        if (await ReadBodyAsync(context.Request, cancellationToken) is not { } body)
        {
            return Refused(StatusCodes.Status413PayloadTooLarge, "too-large", $"Send at most {MaxBodySize / 1024} KB.");
        }

        LabResultsPost? post;
        try
        {
            post = JsonSerializer.Deserialize<LabResultsPost>(body, LabJson.Options);
        }
        catch (JsonException)
        {
            post = null;
        }

        if (post is null || !IsWellFormed(post))
        {
            return Refusal(LabFiling.Malformed);
        }

        LabFiling filing;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            filing = await results.FileFromLabAsync(keyHash, order, post.CollectedAt, [.. post.Results.Select(r => (r.Code, r.Value, r.Unit))], cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return NotSetUp(context);
        }

        if (filing is LabFiling.Filed or LabFiling.AlreadyFiled)
        {
            return Results.NoContent();
        }

        Log(context).LogInformation("A lab's results were refused: {Why}.", Words[filing].Error);
        return Refusal(filing);
    }

    // What the database checks too, refused here before a transaction is started: 1 to MaxResults results, each test
    // given once, with a value from 0 to under 10^8 of at most 4 decimals (what the column holds) and a unit.
    static bool IsWellFormed(LabResultsPost post) =>
        post.CollectedAt != default
        && post.Results.Count is >= 1 and <= MaxResults
        && post.Results.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() == post.Results.Count
        && post.Results.All(r => r.Code.Length is >= 1 and <= 20 && r.Unit.Length is >= 1 and <= 20
            && r.Value is >= 0 and < 100_000_000 && decimal.Round(r.Value, 4) == r.Value);

    // Nothing but since, once, as the digits of a transaction ID; none means from the beginning.
    static bool TrySince(IQueryCollection query, out ulong since)
    {
        since = 0;
        if (query.Keys.Any(name => name != "since"))
        {
            return false;
        }

        if (!query.TryGetValue("since", out var given))
        {
            return true;
        }

        return given.Count == 1 && given[0] is { Length: >= 1 and <= 20 } text && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out since);
    }

    // The body, up to MaxBodySize; null when it is larger. Read here, not by the server's limit alone, which a test
    // server doesn't apply.
    static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodySize)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxBodySize)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return buffer.ToArray();
    }

    // The app has a key, and the lab sent it, but the database holds another, or none: the migration job ran with a
    // different EHR_LAB_KEY. The lab can't fix that, so it is told to try again later, and the log says what to do.
    static IResult NotSetUp(HttpContext context)
    {
        Log(context).LogError("The database doesn't hold the hash of this app's Ehr:Lab:Key: run the migration job with the same key as EHR_LAB_KEY.");
        return Refused(StatusCodes.Status503ServiceUnavailable, "not-ready", "The lab's door isn't ready on this server. Try again later.");
    }

    static IResult Refusal(LabFiling filing) => Refused(Words[filing].Status, Words[filing].Error, Words[filing].Message);

    static IResult Refused(int status, string error, string message) =>
        Results.Json(new LabRefusal(error, message), LabJson.Options, statusCode: status);

    // Each refusal of a filing: its status, its word (the database's) and its sentence.
    static readonly Dictionary<LabFiling, (int Status, string Error, string Message)> Words = new()
    {
        [LabFiling.Malformed] = (StatusCodes.Status400BadRequest, "malformed",
            $"Send collectedAt and 1 to {MaxResults} results, each a test given once, with a value from 0 to under 100,000,000 of at most 4 decimals, and its unit."),
        [LabFiling.UnknownOrder] = (StatusCodes.Status404NotFound, "unknown-order", "No lab order has that ID."),
        [LabFiling.Discontinued] = (StatusCodes.Status409Conflict, "discontinued", "This order was discontinued; it takes no results."),
        [LabFiling.Closed] = (StatusCodes.Status409Conflict, "closed", "This order's results were acknowledged; it takes no more."),
        [LabFiling.DifferentResult] = (StatusCodes.Status409Conflict, "different-result", "A test in this post already has a different result; nothing was filed."),
        [LabFiling.NotReported] = (StatusCodes.Status422UnprocessableEntity, "not-reported", "A test in this post isn't one this order reports; nothing was filed."),
        [LabFiling.WrongUnit] = (StatusCodes.Status422UnprocessableEntity, "wrong-unit", "A result isn't in its test's unit, the one the hand-over names; nothing was filed."),
        [LabFiling.InTheFuture] = (StatusCodes.Status422UnprocessableEntity, "in-the-future",
            "The collection time is ahead of this server's clock; nothing was filed. Check the lab's clock, or send it again later."),
    };

    static ILogger Log(HttpContext context) => context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(LabApi));
}

// The secret a lab's system calls the on-prem app with (Ehr:Lab:Key). The migration job is given the same as
// EHR_LAB_KEY and stores its hash in the database. Optional: without it there is no lab and no door (Program.cs).
public sealed record LabApiKey(string Value)
{
    public const string Setting = "Ehr:Lab:Key";

    public const int MinimumLength = 32;

    public static LabApiKey FromConfiguration(IConfiguration configuration) =>
        configuration[Setting] is { Length: >= MinimumLength } key
            ? new(key)
            : throw new InvalidOperationException(
                $"Set {Setting} to a secret of at least {MinimumLength} characters, the one the migration job is given as EHR_LAB_KEY, or leave it empty for no lab.");
}
