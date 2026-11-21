using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Lab;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Ehr.Domain.Results;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
// Who: the lab, by the key in KeyHeader, hashed and compared in fixed time with the hash of Ehr:Lab:Key (LabApiKey)
// before anything else, and carrying none of the app's credentials beside it: no Authorization header and none of the
// staff, portal or sign-in-in-progress cookies. No account stands behind a lab, so the database checks again, by itself:
// it is given the hash of the key the caller presented, finds it among what the migration job stored, and lets nobody
// signed in through (db/policies/76-lab.sql). The cloud app maps none of this and forwards nothing under /api/lab
// (CloudForwardingMiddleware), so the door is the clinic's network's alone. Requests that present the right key are
// counted in the lab's own rate-limit window; every other request to the door, in a small window of its own, which can
// only ever earn a 401 or a 429, so nobody without the key can make the lab's requests wait or fail (AccountRateLimits).
//
// What a lab gets is only what it needs, and every order handed over is recorded by the database as it is read, with
// the lab as the source (PhiReader.LabOrdersAsync); what it files goes through the same rule a clinician's filing does,
// and is audited the same way, as the lab's. Each request is one transaction. Every answer is no-store JSON, in every
// environment: a refusal is a fixed word and sentence, with nothing from the request or a patient's record, and so is a
// failure (Failures), which never reaches an error page. The log says what was refused or failed, and never the key,
// an order or a patient.
public static class LabApi
{
    public const string Path = "/api/lab";

    // Its own header, not a bearer token: the API paths' authentication (DesktopTokenHandler) would look a bearer token
    // up as a desktop session on every request, and log that it isn't one.
    public const string KeyHeader = "X-Lab-Key";

    // Far more than the most an order's results take.
    public const long MaxBodySize = 16 * 1024;

    public const int MaxResults = 50;

    // The cookies of the app's sign-ins (AccountSchemes), any of which beside the key is refused.
    static readonly string[] SignInSchemes = [AccountSchemes.Staff, AccountSchemes.Patient, AccountSchemes.StaffPending];

    // Mapped only when the app has a key (Program.cs), which it then holds as a LabApiKey.
    public static RouteGroupBuilder MapLabApi(this IEndpointRouteBuilder endpoints)
    {
        // No staff or patient account calls these: the key is the credential, checked here and by the database.
        var group = endpoints.MapGroup(Path).AllowAnonymous().AddEndpointFilter(Failures);
        group.MapGet("/orders", (HttpContext context, PhiReader reader, EhrDbContext db, CancellationToken cancellationToken) =>
            Admitted(context, out var presented) is { } refused ? Task.FromResult(refused) : HandOverAsync(context, reader, db, presented, cancellationToken));
        group.MapPost("/orders/{id:guid}/results", (Guid id, HttpContext context, LabResults results, EhrDbContext db, CancellationToken cancellationToken) =>
                Admitted(context, out var presented) is { } refused ? Task.FromResult(refused) : FileAsync(id, context, results, db, presented, cancellationToken))
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodySize));
        return group;
    }

    // Null when the request comes from the lab: the right key, and none of the app's credentials beside it. Then
    // `presented` is the hex SHA-256 of the key it presented, which the database checks again. Every answer is
    // no-store from here.
    static IResult? Admitted(HttpContext context, out string presented)
    {
        context.Response.Headers.CacheControl = "no-store";
        var hash = LabApiKey.Presented(context.Request);
        presented = Convert.ToHexStringLower(hash);
        var credentials = CarriesCredentials(context);
        if (!credentials && context.RequestServices.GetRequiredService<LabApiKey>().Admits(hash))
        {
            return null;
        }

        Log(context).LogWarning(
            "A request to the lab's door from {Address} was refused: {Why}.", context.Connection.RemoteIpAddress,
            context.Request.Headers[KeyHeader].Count == 0 ? "it had no key" : credentials ? "it carried an account's credentials too" : "its key is wrong");
        return Refused(StatusCodes.Status401Unauthorized, "unauthorized", $"Send the lab's key in {KeyHeader}, and none of the app's sign-ins.");
    }

    // Whether the request carries any way of being signed in to the app: an Authorization header (the desktop app's
    // token, or anything else), or a cookie of one of the app's sign-ins, whole or in chunks, its name in any case. By
    // their presence, not by whether they are still good: the lab has no reason to send any.
    static bool CarriesCredentials(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true || context.Request.Headers.ContainsKey("Authorization"))
        {
            return true;
        }

        var cookies = context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var names = SignInSchemes.Select(scheme => cookies.Get(scheme).Cookie.Name).OfType<string>().ToList();
        return context.Request.Cookies.Keys.Any(cookie => names.Any(name => cookie.StartsWith(name, StringComparison.OrdinalIgnoreCase)));
    }

    static async Task<IResult> HandOverAsync(HttpContext context, PhiReader reader, EhrDbContext db, string presented, CancellationToken cancellationToken)
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
            handOver = await reader.LabOrdersAsync(presented, since, cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (PostgresException e) when (LabStore.IsKeyRefused(e))
        {
            return NotSetUp(context);
        }

        // Through the contract and back, so the answer holds what it names and nothing else.
        var orders = JsonSerializer.Deserialize<List<LabOrder>>(handOver.Orders, LabJson.Options)!;
        return Results.Json(new LabOrders(handOver.Watermark.ToString(CultureInfo.InvariantCulture), handOver.More, orders), LabJson.Options);
    }

    static async Task<IResult> FileAsync(Guid order, HttpContext context, LabResults results, EhrDbContext db, string presented, CancellationToken cancellationToken)
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
            filing = await results.FileFromLabAsync(presented, order, post.CollectedAt, [.. post.Results.Select(r => (r.Code, r.Value, r.Unit))], cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (PostgresException e) when (LabStore.IsKeyRefused(e))
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

    // What the database checks too, refused here before a transaction is started: 1 to MaxResults results, none of them
    // null, each test given once, with a value from 0 to under 10^8 of at most 4 decimals (what the column holds) and a
    // unit. The contract's JSON refuses a missing or null field, but not a null in a list.
    static bool IsWellFormed(LabResultsPost post) =>
        post.CollectedAt != default
        && post.Results is { Count: >= 1 and <= MaxResults }
        && post.Results.All(r => r is { Code.Length: >= 1 and <= 20, Unit.Length: >= 1 and <= 20, Value: >= 0 and < 100_000_000 } && decimal.Round(r.Value, 4) == r.Value)
        && post.Results.Select(r => r.Code).Distinct(StringComparer.Ordinal).Count() == post.Results.Count;

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

    // The app took the key the lab sent, but the database refused it (ehr.require_lab): it holds another key's hash, or
    // none, so the migration job ran with a different EHR_LAB_KEY from this app's Ehr:Lab:Key. The lab can't fix that, so
    // it is told to try again later, and the log says what to do. Any other failure is Failures'.
    static IResult NotSetUp(HttpContext context)
    {
        Log(context).LogError("The database refused the key this app took for the lab: run the migration job with this app's Ehr:Lab:Key as EHR_LAB_KEY.");
        return Refused(StatusCodes.Status503ServiceUnavailable, "not-ready", "The lab's door isn't ready on this server. Try again later.");
    }

    // Anything that fails behind the door answers in the door's shape, in every environment: never the developer
    // exception page, which shows the request's headers, the key among them, nor an error page. The log names the kind
    // of failure, and the database's code when it was the database's, and nothing from the request.
    static async ValueTask<object?> Failures(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        try
        {
            return await next(invocation);
        }
        catch (Exception e) when (e is not OperationCanceledException || !invocation.HttpContext.RequestAborted.IsCancellationRequested)
        {
            Log(invocation.HttpContext).LogError(
                "A request to the lab's door failed: {Failure}{Code}.", e.GetType().Name, e is PostgresException database ? $", SQLSTATE {database.SqlState}" : "");
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
            return Refused(StatusCodes.Status500InternalServerError, "failed",
                "The clinic's server failed to answer. Send it again later: results already filed are answered as filed.");
        }
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
// EHR_LAB_KEY and stores its hash in the database. Optional: without it there is no lab and no door (Program.cs), and
// no LabApiKey. Admits is virtual only so a test can stand in for this check and show the database refuses a wrong key
// by itself.
public record LabApiKey(string Value)
{
    public const string Setting = "Ehr:Lab:Key";

    public const int MinimumLength = 32;

    readonly byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Value));

    public static LabApiKey FromConfiguration(IConfiguration configuration) =>
        configuration[Setting] is { Length: >= MinimumLength } key
            ? new(key)
            : throw new InvalidOperationException(
                $"Set {Setting} to a secret of at least {MinimumLength} characters, the one the migration job is given as EHR_LAB_KEY, or leave it empty for no lab.");

    // The SHA-256 of the key a request presents in LabApi.KeyHeader, or of nothing.
    public static byte[] Presented(HttpRequest request) => SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers[LabApi.KeyHeader].ToString()));

    // Whether the presented key's hash is this key's, compared in fixed time.
    public virtual bool Admits(byte[] presented) => CryptographicOperations.FixedTimeEquals(presented, hash);
}
