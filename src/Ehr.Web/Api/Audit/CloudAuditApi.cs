using System.Security.Cryptography;
using System.Text;
using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;

namespace Ehr.Web.Api.Audit;

// The on-prem app's door for the cloud app's audit events (CloudAuditShipper). POST /api/audit/cloud-events with up to
// MaxEvents events and the shared key as a bearer token; the events are inserted with source "cloud" and their own IDs
// and times, and one already here is skipped. An empty batch is how the cloud app checks the on-prem app is reachable.
public static class CloudAuditApi
{
    public const string Path = "/api/audit/cloud-events";

    public const int MaxEvents = 500;

    public static RouteHandlerBuilder MapCloudAudit(this IEndpointRouteBuilder endpoints, CloudAuditKey key)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key.Value));
        // No staff or patient account ships these: the key is the credential, checked here.
        return endpoints.MapPost(Path, (HttpRequest request, List<AuditEvent> events, EhrDbContext db, CancellationToken cancellationToken) =>
                IsAuthorized(request, expected) ? ImportAsync(events, db, cancellationToken) : Task.FromResult(Results.Unauthorized()))
            .AllowAnonymous();
    }

    static bool IsAuthorized(HttpRequest request, byte[] expected)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(header[scheme.Length..])), expected);
    }

    static async Task<IResult> ImportAsync(List<AuditEvent> events, EhrDbContext db, CancellationToken cancellationToken)
    {
        if (events.Count > MaxEvents || events.Any(e => !IsValid(e)))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["events"] = [$"Send up to {MaxEvents} events, each with an ID, a known action and fields within their limits."],
            });
        }

        foreach (var e in events)
        {
            // Whatever the body says, these came from the cloud app.
            e.Source = AuditSources.Cloud;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AuditEventImport.ImportAsync(db, events, cancellationToken);
        await transaction.CommitAsync(CancellationToken.None);
        return Results.NoContent();
    }

    // The limits of the audit_events columns (AuditEventConfiguration).
    static bool IsValid(AuditEvent e) =>
        e.Id != Guid.Empty
        && AuditActions.All.Contains(e.Action)
        && e.TableName is null or { Length: <= 63 }
        && e.Reason is null or { Length: <= 500 };
}
