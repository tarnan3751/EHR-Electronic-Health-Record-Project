using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.RecordViewTests;

namespace Ehr.Web.Tests.TestCases.UC6;

// Test Case ID: TC-UC6-46
// Purpose / scenario: A record viewed many times is shown 50 views a page, newest first, with "Older views" and "Newer
//   views" as plain links that carry a moment and nothing else; following them reaches every view of the last 12 months
//   once, and none older. A link that names no moment the page wrote, or one past the last view, is answered without an
//   error.
// Type: Functional (alternative flow: many views)
// Requirements: proposed FR-57
// Use case: UC 6, View my results and appointments in the portal
// Preconditions: The patient has a confirmed portal account, signed in on the on-site app, which opened their home. The
//   audit log holds 120 views of the record by Dr. Test, a minute apart, and one 13 months old.
// Inputs and system conditions: GET /portal/views, then each "Older views" link to the end, then each "Newer views"
//   link back; GET /portal/views?older=garbage and ?older= a moment before the 12 months; on the on-site app.
// Test data: The 121 views by Dr. Test, written to the log as the PHI reader writes a read of the patient.
// Steps:
//   1. Open Who viewed my record and follow "Older views" until there is none.
//   2. Follow "Newer views" until there is none.
//   3. Open the page with a cursor that isn't a moment, and with one older than 12 months.
// Expected result: Step 1: three pages of 50, 50 and 21 lines, together the patient's own view and the 120 by Dr.
//   Test, each once, newest first; the last page has no "Older views", and the newest no "Newer views". Each link is
//   /portal/views with only "older" or "newer" and a moment to the microsecond in UTC. Step 2: the same pages in
//   reverse order, then one of the patient's own looks at this page since, which has no "Newer views". Step 3: the
//   newest page; then "No more views to show." with a link to the newest views.
// Creator: Tahseen Arnan
public partial class TC_UC6_46_WhoViewedPages(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Pages_of_views_reach_every_view_of_the_last_year_once()
    {
        var (clinician, _, patient) = await ViewedPatientAsync(apps, "Paged");
        await ReadsAsync(apps, patient.Patient, clinician.Id, 120);
        await ReadsAsync(apps, patient.Patient, clinician.Id, 1, newestMinutesAgo: 60 * 24 * 396);

        // Step 1.
        var older = new List<List<string>>();
        var html = await PageAsync(patient.Client);
        while (true)
        {
            older.Add(Lines(html));
            var (_, next) = Links(html);
            if (next is null)
            {
                break;
            }

            Assert.Matches(Cursor(), Uri.UnescapeDataString(next));
            html = await patient.Client.GetStringAsync(next, TestContext.Current.CancellationToken);
            Assert.True(older.Count < 5, "Paging didn't end.");
        }

        Assert.Equal([50, 50, 21], older.Select(page => page.Count));
        var all = older.SelectMany(page => page).ToList();
        Assert.Equal(120, all.Count(line => line.StartsWith("staff | Dr. Test, clinician | Your record | ", StringComparison.Ordinal)));
        Assert.Single(all, line => line.StartsWith("you | ", StringComparison.Ordinal));
        Assert.Null(Links(await PageAsync(patient.Client)).Newer);

        // Step 2.
        var newer = new List<List<string>> { Lines(html) };
        while (Links(html).Newer is { } next)
        {
            Assert.Matches(Cursor(), Uri.UnescapeDataString(next));
            html = await patient.Client.GetStringAsync(next, TestContext.Current.CancellationToken);
            newer.Add(Lines(html));
            Assert.True(newer.Count < 5, "Paging didn't end.");
        }

        // The pages back hold the same views, then a page of the patient's own looks at this page, made since.
        Assert.Equal(4, newer.Count);
        Assert.Equal(new[] { older[2], older[1], older[0] }, newer.Take(3));
        Assert.All(newer[3], line => Assert.StartsWith(Looks, line, StringComparison.Ordinal));

        // Step 3.
        // The newest page again: the look just made takes a place at its top, so one line less of the rest fits.
        var plain = Others(await PageAsync(patient.Client));
        var garbage = Others(await PageAsync(patient.Client, "?older=garbage"));
        Assert.Equal(plain.Take(plain.Count - 1), garbage);
        var past = await PageAsync(patient.Client, "?older=" + Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMonths(-13).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(Lines(past));
        Assert.Contains("No more views to show.", Words(past));
        Assert.Contains("href=\"/portal/views\"", past);
    }

    // The patient's own looks at this page, which each GET adds to the top.
    const string Looks = "you | You | Who viewed your record";

    static List<string> Others(string html) => [.. Lines(html).Where(line => !line.StartsWith(Looks, StringComparison.Ordinal))];

    [System.Text.RegularExpressions.GeneratedRegex(@"^/portal/views\?(older|newer)=\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$")]
    private static partial System.Text.RegularExpressions.Regex Cursor();
}
