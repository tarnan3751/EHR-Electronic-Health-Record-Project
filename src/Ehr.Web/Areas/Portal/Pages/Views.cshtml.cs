using System.Globalization;
using Ehr.Data.Audit;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// Who viewed my record (the expansion plan's step 13c, proposed FR-57): the views of the record the session is looking
// at, from the audit log, newest first, 50 at a time: who (a staff member's name and role, "You", a proxy by the address
// the patient gave access to, or the lab), when, which part, and whether under emergency access. Which patient comes
// from the session, never from the request (AGENTS.md); the database answers only for the session's own record, and only
// with the lines its viewer may see, each naming the part of the record the read was recorded for (step 13c2), or the
// record to a viewer who may not be told it (ehr.record_views, db/policies/42-record-views.sql). A page is asked for by a moment
// alone, older or newer than the page before, so no ID is ever in an address. The read goes through the PHI reader,
// which records it. Off-site it comes from the cloud copy, which a view from the last few seconds may not have reached.
public class ViewsModel(PortalSubject subject, PhiReader phi) : PageModel
{
    // How far back the database looks (42-record-views.sql), as the page says it.
    public const int Months = 12;

    // How the page writes a moment into its links, and the one form it reads back.
    const string MomentFormat = "yyyy-MM-ddTHH:mm:ss.ffffffZ";

    // "Older views" and "Newer views": the page before's last or first moment. Anything else is the newest page.
    [BindProperty(SupportsGet = true)]
    public string? Older { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Newer { get; set; }

    public bool Available { get; private set; }

    // The page's lines, newest first: views by the same person of the same part in the same minute are one line (Collapse).
    public IReadOnlyList<ViewLine> Lines { get; private set; } = [];

    // The moments the links to the next pages start from, when there is a page that way.
    public string? OlderFrom { get; private set; }

    public string? NewerFrom { get; private set; }

    // Asked for a page past the views there are, such as from an old link: the page offers the newest instead.
    public bool PastTheEnd { get; private set; }

    public bool Own => subject.Acting is null;

    // The patient always; a proxy only when the patient allowed them sensitive items. Only these are shown views from
    // outside the care team, emergency access among them (42-record-views.sql), so only these are told what it means.
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        var older = Moment(Older);
        var newer = older is null ? Moment(Newer) : null;
        var views = await phi.RecordViewsAsync(subject.PatientId, older, newer, HttpContext.RequestAborted);
        Available = views is not null;
        if (views is not { Count: > 0 })
        {
            PastTheEnd = older is not null || newer is not null;
            return;
        }

        Lines = Collapse(views, Own);
        // Going older (or from the newest), there is more that way when the database says so, and newer whenever this
        // page began from a moment; going newer, the other way round.
        var more = views[0].More;
        var (olderPage, newerPage) = newer is null ? (more, older is not null) : (true, more);
        OlderFrom = olderPage ? Format(views[^1].OccurredAt) : null;
        NewerFrom = newerPage ? Format(views[0].OccurredAt) : null;
    }

    static DateTimeOffset? Moment(string? text) =>
        DateTimeOffset.TryParseExact(text, MomentFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            ? moment
            : null;

    static string Format(DateTimeOffset moment) => moment.UtcDateTime.ToString(MomentFormat, CultureInfo.InvariantCulture);

    // One line of the page.
    public sealed record ViewLine(string Who, string? Role, string Part, bool Emergency, DateTimeOffset At, string Kind);

    // Views by the same person of the same part in the same minute, one after another, are one line. A part whose page
    // records one read a view is counted, and its views that showed sensitive items are a line apart from those that
    // didn't. A part whose page records several (the chart with each note it lists, a note's page, the visit summaries)
    // is one line of the views in that minute, with no count, which says it showed sensitive items when any of them did.
    static List<ViewLine> Collapse(IReadOnlyList<RecordViewRow> views, bool own)
    {
        var lines = new List<ViewLine>();
        var start = 0;
        for (var i = 1; i <= views.Count; i++)
        {
            if (i < views.Count && Same(views[start], views[i]))
            {
                continue;
            }

            var first = views[start];
            var sensitive = views.Skip(start).Take(i - start).Any(view => view.Sensitive);
            lines.Add(new ViewLine(Who(first, own), Role(first.Role), PartWords(first.Part, i - start, sensitive), first.Emergency, first.OccurredAt, first.Who));
            start = i;
        }

        return lines;
    }

    static bool Same(RecordViewRow a, RecordViewRow b) =>
        (a.Who, a.Name, a.Role, a.Part, a.Emergency) == (b.Who, b.Name, b.Role, b.Part, b.Emergency)
        && (!Words(a.Part).Counted || a.Sensitive == b.Sensitive)
        && Minute(a.OccurredAt) == Minute(b.OccurredAt);

    static DateTime Minute(DateTimeOffset moment) => new(moment.UtcDateTime.Ticks - moment.UtcDateTime.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    static string Who(RecordViewRow view, bool own) => view.Who switch
    {
        "you" => "You",
        "lab" => "The lab",
        "staff" => view.Name ?? "A member of staff",
        // Only the patient is shown a proxy's views (42-record-views.sql), by the address they gave access to. The other
        // words can't occur at runtime: every account that reads a record is a staff or a portal account.
        _ => view.Name is { } address && own ? address : "Someone the record is shared with",
    };

    static string? Role(string? role) => role switch
    {
        null => null,
        "clinician" => "clinician",
        "administrator" => "administrator",
        _ => "member of staff",
    };

    // A part's words for one view, named as the page or list it was, the same for the patient and a proxy; for a counted
    // part, the same with how many ("{0}"), for a line of several; and, for a part a view of which can show the patient's
    // sensitive items, the words for a line that did. A proxy without sensitive items or messages is never given a part
    // that would tell them of either: the database names such a view "record" (42-record-views.sql).
    public sealed record Wording(string One, string? Many = null, Wording? Sensitive = null)
    {
        public bool Counted => Many is not null;
    }

    // Counted: the words, then how many times.
    static Wording Counted(string words, Wording? sensitive = null) => new(words, words + ", {0} times", sensitive);

    // One line a minute, however many reads its page recorded.
    static Wording Once(string words, Wording? sensitive = null) => new(words, Sensitive: sensitive);

    // The words for each part the database names, in one place: the parts a read is recorded for (AuditParts, step 13c2),
    // and those a view recorded without its part is named by (RecordViewParts), which are as before that step. One the
    // page doesn't know, as from a database newer than the app during a deploy, is the record.
    internal static readonly Dictionary<string, Wording> Parts = new()
    {
        [AuditParts.Chart] = Once("Chart", Once("Chart, with sensitive items")),
        [AuditParts.Lists] = Counted("Allergies, medications and diagnoses", Counted("Allergies, medications and diagnoses, with sensitive items")),
        [AuditParts.Orders] = Counted("Test and medicine orders"),
        [AuditParts.Results] = Counted("Test results"),
        [AuditParts.Vitals] = Counted("Vital signs"),
        [AuditParts.Search] = Counted("Name in a patient search"),
        [AuditParts.Panel] = Counted("Name on a patient list"),
        [AuditParts.Schedule] = Counted("Name on a schedule"),
        [AuditParts.Inbox] = Counted("Name on a task list"),
        [AuditParts.Appointments] = Counted("Appointments"),
        [AuditParts.Home] = Counted("Home page"),
        [AuditParts.Conversations] = Counted("Messages"),
        [AuditParts.Access] = Counted("Sharing"),
        [AuditParts.VisitNote] = Once("Visit note", Once("Visit note, with sensitive items")),
        [AuditParts.NoteDraft] = Once("Draft visit note"),
        [AuditParts.Offline] = Once("Chart, saved for offline use"),
        [AuditParts.Fhir] = Counted("Record, through the health app connection (FHIR)", Counted("Record with sensitive items, through the health app connection (FHIR)")),
        [AuditParts.Summaries] = Once("Visit summaries", Once("Visit summaries, with sensitive items")),
        [AuditParts.Printable] = Once("Printable record", Once("Printable record, with sensitive items")),
        [AuditParts.Download] = Counted("Record download", Counted("Record download, with sensitive items")),
        [AuditParts.Views] = Counted("Who viewed the record"),
        [AuditParts.Refills] = Counted("Refill requests", Counted("Refill requests, with sensitive items")),
        // Pre-visit forms (step 13e), always sensitive items. A view that showed them is a view of sensitive items; a
        // plain read is a forms page that showed none of them (the clinician's with them hidden, which shows how many,
        // or the portal's for a session that may not see them, which shows none), worded as that page. Counted as views,
        // never as forms. Nothing records a read of a form's own rows (they share this key, by their tables): if one
        // ever did as a plain read, it would be worded as the page with the forms hidden, since only a read of sensitive
        // items is marked sensitive (42-record-views.sql).
        [AuditParts.Forms] = Counted("Pre-visit forms list, without the forms", Counted("Pre-visit forms")),
        [RecordViewParts.Record] = Counted("Record"),
        [RecordViewParts.RecordSensitive] = Counted("Record, with sensitive items"),
        [RecordViewParts.DownloadSensitive] = Counted("Record download, with sensitive items"),
        [RecordViewParts.Note] = new("Visit note", "{0} visit notes"),
        [RecordViewParts.Summary] = new("Visit summary", "{0} visit summaries"),
        [RecordViewParts.Messages] = new("Message conversation", "{0} message conversations"),
        [RecordViewParts.LabOrder] = new("Lab order", "{0} lab orders"),
    };

    static Wording Words(string part) => Parts.GetValueOrDefault(part) ?? Parts[RecordViewParts.Record];

    // The words for a line: a part's, for how many views, and whether any showed the patient's sensitive items (said by
    // the part's own name when it was recorded without one: "record-sensitive").
    internal static string PartWords(string part, int count, bool sensitive)
    {
        var words = Words(part);
        words = sensitive && words.Sensitive is { } shown ? shown : words;
        return count > 1 && words.Many is not null ? string.Format(CultureInfo.InvariantCulture, words.Many, count) : words.One;
    }
}
