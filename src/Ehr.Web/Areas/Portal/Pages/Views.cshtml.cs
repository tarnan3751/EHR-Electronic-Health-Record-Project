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

    // Whose record this is, when it isn't the signed-in account's own (FR-19).
    public string? ActingFor => subject.Acting?.PatientName;

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
            lines.Add(new ViewLine(Who(first, own), Role(first.Role), PartWords(first.Part, own, i - start, sensitive), first.Emergency, first.OccurredAt, first.Who));
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

    // A part's words: the patient's and a proxy's for one view; for a counted part, the same with how many ("{0}"), for
    // a line of several; and, for a part a view of which can show the patient's sensitive items, the words for a line
    // that did. A proxy without sensitive items or messages is never given a part that would tell them of either: the
    // database names such a view "record" (42-record-views.sql).
    public sealed record Wording(string Own, string Proxy, string? OwnMany = null, string? ProxyMany = null, Wording? Sensitive = null)
    {
        public bool Counted => OwnMany is not null;
    }

    // Counted: the words, then how many times.
    static Wording Counted(string own, string proxy, Wording? sensitive = null) => new(own, proxy, own + ", {0} times", proxy + ", {0} times", sensitive);

    // One line a minute, however many reads its page recorded.
    static Wording Once(string own, string proxy, Wording? sensitive = null) => new(own, proxy, Sensitive: sensitive);

    // The words for each part the database names, in one place: the parts a read is recorded for (AuditParts, step 13c2),
    // and those a view recorded without its part is named by (RecordViewParts), which are as before that step. One the
    // page doesn't know, as from a database newer than the app during a deploy, is the record.
    internal static readonly Dictionary<string, Wording> Parts = new()
    {
        [AuditParts.Chart] = Once("Your chart: the summary page, which lists your visit notes", "The chart: the summary page, which lists the visit notes",
            Once("Your chart, with its sensitive items: the summary page, which lists your visit notes",
                "The chart, with its sensitive items: the summary page, which lists the visit notes")),
        [AuditParts.Lists] = Counted("Your allergies, medications and diagnoses", "The allergies, medications and diagnoses",
            Counted("Your allergies, medications and diagnoses, with the sensitive ones", "The allergies, medications and diagnoses, with the sensitive ones")),
        [AuditParts.Orders] = Counted("Your orders for tests and medicines", "The orders for tests and medicines"),
        [AuditParts.Results] = Counted("Your test results", "The test results"),
        [AuditParts.Vitals] = Counted("Your vital signs", "The vital signs"),
        [AuditParts.Search] = Counted("Your name, in a search for patients", "The patient's name, in a search for patients"),
        [AuditParts.Panel] = Counted("Your name, in their list of patients", "The patient's name, in their list of patients"),
        [AuditParts.Schedule] = Counted("Your name, on their day's appointments", "The patient's name, on their day's appointments"),
        [AuditParts.Inbox] = Counted("Your name, on their list of tasks", "The patient's name, on their list of tasks"),
        [AuditParts.Appointments] = Counted("Your appointments", "The appointments"),
        [AuditParts.Home] = Counted("Your portal home page, with your appointments", "The portal home page, with the appointments"),
        [AuditParts.Conversations] = Counted("Your messages", "The messages"),
        [AuditParts.Access] = Counted("Who you share your record with", "Who the record is shared with"),
        [AuditParts.VisitNote] = Once("A visit note", "A visit note", Once("A visit note, with sensitive items", "A visit note, with sensitive items")),
        [AuditParts.NoteDraft] = Once("A visit note, being written", "A visit note, being written"),
        [AuditParts.Offline] = Once("Your chart, copied to a clinician's computer for use offline", "The chart, copied to a clinician's computer for use offline"),
        [AuditParts.Fhir] = Counted("Your record, read through the clinic's FHIR connection", "The record, read through the clinic's FHIR connection",
            Counted("Your record, with its sensitive items, read through the clinic's FHIR connection",
                "The record, with its sensitive items, read through the clinic's FHIR connection")),
        [AuditParts.Summaries] = Once("Your visit summaries", "The visit summaries", Once("Your visit summaries, with the sensitive ones", "The visit summaries, with the sensitive ones")),
        [AuditParts.Printable] = Once("Your record, the page to print", "The record, the page to print",
            Once("Your record, the page to print with its sensitive items", "The record, the page to print with its sensitive items")),
        [AuditParts.Download] = new("Your record, downloaded", "The record, downloaded", "Your record, downloaded {0} times", "The record, downloaded {0} times",
            new("Your record, downloaded with its sensitive items", "The record, downloaded with its sensitive items",
                "Your record, downloaded with its sensitive items {0} times", "The record, downloaded with its sensitive items {0} times")),
        [AuditParts.Views] = Counted("Who viewed your record", "Who viewed the record"),
        [AuditParts.Refills] = Counted("Your refill requests", "The refill requests",
            Counted("Your refill requests, with the sensitive ones", "The refill requests, with the sensitive ones")),
        // Pre-visit forms (step 13e), always sensitive items. A view that showed them is a view of sensitive items; a
        // plain read is a forms page that showed none of them (the clinician's with them hidden, which shows how many,
        // or the portal's for a session that may not see them, which shows none), worded as that page. Counted as views,
        // never as forms. A read of a form's own rows (RecordViewParts.Form, the same key) shows the form, so it is a view
        // of sensitive items, worded as the forms.
        [AuditParts.Forms] = Counted("Your pre-visit forms page, with the forms hidden", "The pre-visit forms page, with the forms hidden",
            Counted("Your pre-visit forms", "The pre-visit forms")),
        [RecordViewParts.Record] = new("Your record", "The record", "Your record, {0} times", "The record, {0} times"),
        [RecordViewParts.RecordSensitive] = new("Your record, with its sensitive items", "The record, with its sensitive items",
            "Your record, with its sensitive items, {0} times", "The record, with its sensitive items, {0} times"),
        [RecordViewParts.DownloadSensitive] = new("Your record, downloaded with its sensitive items", "The record, downloaded with its sensitive items",
            "Your record, downloaded with its sensitive items {0} times", "The record, downloaded with its sensitive items {0} times"),
        [RecordViewParts.Note] = new("A visit note", "A visit note", "{0} visit notes", "{0} visit notes"),
        [RecordViewParts.Summary] = new("A visit summary", "A visit summary", "{0} visit summaries", "{0} visit summaries"),
        [RecordViewParts.Messages] = new("A conversation in your messages", "A conversation in the messages",
            "{0} conversations in your messages", "{0} conversations in the messages"),
        [RecordViewParts.LabOrder] = new("A lab order, sent for testing", "A lab order, sent for testing", "{0} lab orders, sent for testing", "{0} lab orders, sent for testing"),
    };

    static Wording Words(string part) => Parts.GetValueOrDefault(part) ?? Parts[RecordViewParts.Record];

    // The words for a line: a part's, the patient's or a proxy's, for how many views, and whether any showed the patient's
    // sensitive items (said by the part's own name when it was recorded without one: "record-sensitive").
    internal static string PartWords(string part, bool own, int count, bool sensitive)
    {
        var words = Words(part);
        words = sensitive && words.Sensitive is { } shown ? shown : words;
        var many = own ? words.OwnMany : words.ProxyMany;
        return count > 1 && many is not null ? string.Format(CultureInfo.InvariantCulture, many, count) : own ? words.Own : words.Proxy;
    }
}
