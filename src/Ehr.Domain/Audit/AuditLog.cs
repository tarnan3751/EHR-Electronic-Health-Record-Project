using Ehr.Data;
using Ehr.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Audit;

// The audit log as admins review it: the newest events first, narrowed by action, actor, patient or time, a page at
// a time. Row-level security hands an admin every event and anyone else none, so nothing here checks who asks. Actors
// are named from the account tables, which admins may read. Patients appear by ID only: an admin has no access to
// patient data (db/policies/30-rls.sql), and asks a clinician when a review needs the name; the values a write changed,
// which the trigger recorded, are the one place an admin sees any, and never those of a sensitive item: for such a write
// the page says that a sensitive item changed and who changed it.
public sealed class AuditLog(EhrDbContext db)
{
    public const int PageSize = 100;

    public async Task<AuditPage> ReadAsync(AuditFilter filter, CancellationToken cancellationToken)
    {
        var events = db.AuditEvents.AsNoTracking();
        if (filter.Action is { Length: > 0 })
        {
            events = events.Where(e => e.Action == filter.Action);
        }

        if (filter.ActorId is { } actor)
        {
            events = events.Where(e => e.ActorId == actor);
        }

        if (filter.PatientId is { } patient)
        {
            events = events.Where(e => e.PatientId == patient);
        }

        if (filter.From is { } from)
        {
            events = events.Where(e => e.OccurredAt >= from);
        }

        if (filter.Before is { } before)
        {
            events = events.Where(e => e.OccurredAt < before);
        }

        // Every column but the values a write changed, which the runtime roles can't read (db/policies/10-grants.sql).
        var page = await events
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(PageSize + 1)
            .Select(e => new AuditEvent
            {
                Id = e.Id,
                OccurredAt = e.OccurredAt,
                ActorId = e.ActorId,
                Action = e.Action,
                TableName = e.TableName,
                RowId = e.RowId,
                PatientId = e.PatientId,
                Source = e.Source,
                Reason = e.Reason,
                Sensitive = e.Sensitive,
            })
            .ToListAsync(cancellationToken);
        var more = page.Count > PageSize;
        if (more)
        {
            page.RemoveAt(PageSize);
        }

        // The values, for the events an admin may see them of: never a sensitive item's, nor the lists' history rows,
        // which copy those items (ehr.shown_audit_details, db/policies/40-audit.sql). The page says so in their place.
        var ids = page.Select(e => e.Id).ToArray();
        var details = await db.Database
            .SqlQuery<ShownDetails>($"SELECT id AS \"Id\", details::text AS \"Details\" FROM ehr.shown_audit_details({ids})")
            .ToDictionaryAsync(shown => shown.Id, shown => shown.Details, cancellationToken);
        foreach (var e in page)
        {
            e.Details = details.GetValueOrDefault(e.Id);
        }

        var actorIds = page.Where(e => e.ActorId is not null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var actors = await db.StaffUsers.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", cancellationToken);
        foreach (var account in await db.PatientUsers.AsNoTracking()
                     .Where(u => actorIds.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync(cancellationToken))
        {
            actors[account.Id] = $"Portal account {account.Email}";
        }

        return new AuditPage(page, actors, more);
    }
}

sealed record ShownDetails(Guid Id, string? Details);

// What to show: one action or any; one staff or patient account or anyone; one patient or any; from a moment on;
// before a moment (also how the next, older page is asked for).
public sealed record AuditFilter(string? Action, Guid? ActorId, Guid? PatientId, DateTimeOffset? From, DateTimeOffset? Before);

// A page of events, newest first, the names of the accounts that acted, and whether older events remain.
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, IReadOnlyDictionary<Guid, string> Actors, bool More);
