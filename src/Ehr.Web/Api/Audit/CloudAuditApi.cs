using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Domain.Phi;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Api.Audit;

// The on-prem app's door for the cloud app's audit events (CloudAuditShipper). POST /api/audit/cloud-events with up to
// MaxEvents events and the shared key in KeyHeader; the events are inserted with source "cloud" and their own IDs
// and times, and one already here is skipped. An empty batch is how the cloud app checks the on-prem app can save. The
// cloud app forwards anyone's POST here, so the key is checked before the body is read, and the body is capped at
// MaxBodySize. The audit log is append-only, so only what the cloud app records is taken: reads of one patient by the
// PHI reader. Every other event is recorded on a POST, which the cloud app forwards here rather than queues.
public static class CloudAuditApi
{
    public const string Path = "/api/audit/cloud-events";

    // Its own header, not a bearer token: the API paths' authentication (DesktopTokenHandler) would look a bearer token
    // up as a desktop session on every check-in, and log that it isn't one.
    public const string KeyHeader = "X-Cloud-Audit-Key";

    public const int MaxEvents = 500;

    // Comfortably above MaxEvents events.
    public const long MaxBodySize = 1024 * 1024;

    // How far ahead of this server's clock an event may be. There's no limit the other way: events wait in the cloud
    // app's queue for as long as an outage lasts.
    static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    public static RouteHandlerBuilder MapCloudAudit(this IEndpointRouteBuilder endpoints, CloudAuditKey key)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        // No staff or patient account ships these: the key is the credential, checked here.
        return endpoints.MapPost(Path, (HttpRequest request, EhrDbContext db, CancellationToken cancellationToken) =>
                IsAuthorized(request, expected) ? ImportAsync(request, db, cancellationToken) : Task.FromResult(Results.Unauthorized()))
            .AllowAnonymous()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodySize));
    }

    static bool IsAuthorized(HttpRequest request, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(request.Headers[KeyHeader].ToString())), expected);

    static async Task<IResult> ImportAsync(HttpRequest request, EhrDbContext db, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        List<AuditEvent>? events;
        try
        {
            events = await request.ReadFromJsonAsync<List<AuditEvent>>(cancellationToken);
        }
        catch (JsonException)
        {
            events = null;
        }

        // A bad event refuses the whole batch: the cloud app sends it again until it's taken, so nothing in it can be
        // half-stored.
        if (events is null || events.Count > MaxEvents || events.Any(e => !IsCloudRead(e)))
        {
            return Refused($"Send up to {MaxEvents} events, each a read of one patient by a signed-in account, with no reason or details, and not from the future.");
        }

        // On its own, so the cloud app's log tells a clock that runs ahead from a bug.
        var now = DateTimeOffset.UtcNow;
        if (events.Any(e => e.OccurredAt > now + ClockSkew))
        {
            return Refused($"An event is more than {ClockSkew.TotalMinutes} minutes ahead of this server's clock: check the cloud server's clock.");
        }

        foreach (var e in events)
        {
            // Whatever the body says, these came from the cloud app.
            e.Source = AuditSources.Cloud;
        }

        // Even for an empty batch: the shipper's check-in has to fail while the primary is down.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuditEventImport.ImportAsync(db, events, cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return Results.NoContent();
    }

    static IResult Refused(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["events"] = [message] });

    // What PhiReader records: a read of the patient's row, by the signed-in account (row-level security returns no
    // patient to anyone else), with no reason or details. The cloud app never produces anything else. The JSON can
    // hold null where an event should be.
    static bool IsCloudRead(AuditEvent? e) =>
        e is not null
        && e.Id != Guid.Empty
        && e.Action == AuditActions.Read
        && e.TableName == PhiReader.PatientsTable
        && e.ActorId is { } actor && actor != Guid.Empty
        && e.PatientId is { } patient && patient != Guid.Empty && e.RowId == patient
        && e.Reason is null
        && e.Details is null
        && e.OccurredAt != default;
}
