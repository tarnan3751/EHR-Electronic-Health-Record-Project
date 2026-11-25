using Ehr.Data;
using Ehr.Data.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Audit;

// The audit log as admins review it: the newest events first, narrowed by action, actor, patient, the part of the
// record a read was for (step 13c2), an emergency access's review (step 18) or time, a page at a time. Row-level security hands an admin every event and anyone else none, so nothing here checks who asks. Actors
// are named from the account tables, which admins may read. Patients appear by ID only: an admin has no access to
// patient data (db/policies/30-rls.sql), and asks a clinician when a review needs the name. Of a write to patient data
// an admin gets the names of the fields it changed, never a value (step 18a), and of a sensitive item's not even those:
// the page says that a sensitive item changed and who changed it. An event about no patient, such as a desktop sign-in,
// keeps its details.
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

        if (filter.Part is { Length: > 0 })
        {
            events = events.Where(e => e.Part == filter.Part);
        }

        // Emergency accesses by their review (step 18): those waiting for one, or those reviewed as not appropriate.
        if (filter.Review == AuditReviewFilter.Waiting)
        {
            events = events.Where(e => e.Action == AuditActions.BreakGlass && !db.EmergencyAccessReviews.Any(r => r.Id == e.RowId));
        }
        else if (filter.Review == AuditReviewFilter.NotAppropriate)
        {
            events = events.Where(e => e.Action == AuditActions.BreakGlass
                && db.EmergencyAccessReviews.Any(r => r.Id == e.RowId && r.Outcome == EmergencyAccessOutcomes.NotAppropriate));
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
                Part = e.Part,
            })
            .ToListAsync(cancellationToken);
        var more = page.Count > PageSize;
        if (more)
        {
            page.RemoveAt(PageSize);
        }

        // What an admin may see of each (ehr.shown_audit_details, db/policies/40-audit.sql): an event about no patient's
        // details, and a write to patient data's changed fields by name (step 18a); nothing of a sensitive item's, the
        // lists' history rows, messages or refill requests. The page says so in their place.
        var ids = page.Select(e => e.Id).ToArray();
        var shown = await db.Database
            .SqlQuery<ShownDetails>(
                $"SELECT id AS \"Id\", details::text AS \"Details\", array_to_string(fields, ', ') AS \"Fields\" FROM ehr.shown_audit_details({ids})")
            .ToListAsync(cancellationToken);
        var details = shown.ToDictionary(s => s.Id, s => s.Details);
        foreach (var e in page)
        {
            e.Details = details.GetValueOrDefault(e.Id);
        }

        var fields = shown.Where(s => s.Fields is not null).ToDictionary(s => s.Id, s => s.Fields!);

        var actorIds = page.Where(e => e.ActorId is not null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var actors = await db.StaffUsers.AsNoTracking()
            .Where(u => actorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.DisplayName} ({u.Email})", cancellationToken);
        foreach (var account in await db.PatientUsers.AsNoTracking()
                     .Where(u => actorIds.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToListAsync(cancellationToken))
        {
            actors[account.Id] = $"Portal account {account.Email}";
        }

        return new AuditPage(page, actors, more, fields);
    }
}

sealed record ShownDetails(Guid Id, string? Details, string? Fields);

// What to show: one action or any; one staff or patient account or anyone; one patient or any; from a moment on;
// before a moment (also how the next, older page is asked for); reads for one part of the record or any; emergency
// accesses waiting for review or reviewed as not appropriate, or any event (AuditReviewFilter).
public sealed record AuditFilter(string? Action, Guid? ActorId, Guid? PatientId, DateTimeOffset? From, DateTimeOffset? Before, string? Part = null,
    string? Review = null);

// The audit page's choice of emergency accesses by their review (step 18).
public static class AuditReviewFilter
{
    public const string Waiting = "waiting";

    public const string NotAppropriate = EmergencyAccessOutcomes.NotAppropriate;
}

// A page of events, newest first, the names of the accounts that acted, whether older events remain, and for each write
// to patient data the names of the fields it changed, comma-separated (step 18a).
public sealed record AuditPage(IReadOnlyList<AuditEvent> Events, IReadOnlyDictionary<Guid, string> Actors, bool More,
    IReadOnlyDictionary<Guid, string> Fields);
