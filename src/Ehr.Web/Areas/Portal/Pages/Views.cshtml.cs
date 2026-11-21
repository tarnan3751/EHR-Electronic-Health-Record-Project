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
// with the lines its viewer may see (ehr.record_views, db/policies/42-record-views.sql). A page is asked for by a moment
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

    // The page's lines, newest first: views by the same person of the same part in the same minute are one line, with
    // how many there were.
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
            lines.Add(new ViewLine(Who(first, own), Role(first.Role), Part(first.Part, own, i - start), first.Emergency, first.OccurredAt, first.Who));
            start = i;
        }

        return lines;
    }

    static bool Same(RecordViewRow a, RecordViewRow b) =>
        (a.Who, a.Name, a.Role, a.Part, a.Emergency) == (b.Who, b.Name, b.Role, b.Part, b.Emergency)
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

    // The words for each part the database names, the patient's and a proxy's, for one view and for several in the same
    // minute. One it doesn't know is the record. Until a later step records which page was opened, most views are the
    // record: the chart, results, vital signs and appointments are all read as the patient's row.
    static readonly Dictionary<string, (string Own, string Proxy, string OwnMany, string ProxyMany)> Parts = new()
    {
        [RecordViewParts.Record] = ("Your record", "The record", "Your record, {0} times", "The record, {0} times"),
        [RecordViewParts.RecordSensitive] = ("Your record, with its sensitive items", "The record, with its sensitive items",
            "Your record, with its sensitive items, {0} times", "The record, with its sensitive items, {0} times"),
        [RecordViewParts.Download] = ("Your record, downloaded", "The record, downloaded", "Your record, downloaded {0} times", "The record, downloaded {0} times"),
        [RecordViewParts.DownloadSensitive] = ("Your record, downloaded with its sensitive items", "The record, downloaded with its sensitive items",
            "Your record, downloaded with its sensitive items {0} times", "The record, downloaded with its sensitive items {0} times"),
        [RecordViewParts.Note] = ("A visit note", "A visit note", "{0} visit notes", "{0} visit notes"),
        [RecordViewParts.Summary] = ("A visit summary", "A visit summary", "{0} visit summaries", "{0} visit summaries"),
        [RecordViewParts.Messages] = ("A conversation in your messages", "A conversation in the messages",
            "{0} conversations in your messages", "{0} conversations in the messages"),
        [RecordViewParts.LabOrder] = ("A lab order, sent for testing", "A lab order, sent for testing", "{0} lab orders, sent for testing", "{0} lab orders, sent for testing"),
        [RecordViewParts.Views] = ("Who viewed your record", "Who viewed the record", "Who viewed your record, {0} times", "Who viewed the record, {0} times"),
    };

    static string Part(string part, bool own, int count)
    {
        var words = Parts.GetValueOrDefault(part, Parts[RecordViewParts.Record]);
        return count == 1
            ? own ? words.Own : words.Proxy
            : string.Format(CultureInfo.InvariantCulture, own ? words.OwnMany : words.ProxyMany, count);
    }
}
