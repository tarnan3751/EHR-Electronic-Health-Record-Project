using Ehr.Data;
using Ehr.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Audit;

// The audit log as admins review it: the newest events first, narrowed by action, actor, patient or time, a page at
// a time. Row-level security hands an admin every event and anyone else none, so nothing here checks who asks. Actors
// are named from the account tables, which admins may read. Patients appear by ID only: an admin has no access to
// patient data (db/policies/30-rls.sql), and asks a clinician when a review needs the name; the values a write changed,
// which the trigger recorded, are the one place an admin sees any.
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

        var page = await events
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(PageSize + 1)
            .ToListAsync(cancellationToken);
        var more = page.Count > PageSize;
        if (more)
        {
            page.RemoveAt(PageSize);
        }

        var actorIds = page.Where(e => e.ActorId is not null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var actors = await db.StaffUsers.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", cancellationToken);
        foreach (var account in await db.PatientUsers.AsNoTracking()
                     .Where(u => actorIds.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync(cancellationToken))
        {
            actors[account.Id] = $"Patient account {account.Email}";
        }

        return new AuditPage(page, actors, more);
    }
}

// What to show: one action or any; one staff or patient account or anyone; one patient or any; from a moment on;
// before a moment (also how the next, older page is asked for).
public sealed record AuditFilter(string? Action, Guid? ActorId, Guid? PatientId, DateTimeOffset? From, DateTimeOffset? Before);

// A page of events, newest first, the names of the accounts that acted, and whether older events remain.
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, IReadOnlyDictionary<Guid, string> Actors, bool More);
