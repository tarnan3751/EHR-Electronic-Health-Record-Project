using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ProxyTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests;

// PR 5's polish. After an action, the notice that answers it takes the focus (docs/brand: data-focus-on-load), not a
// field that autofocus would otherwise pick: on an order's results page and the clinic's proxy page. And the band of
// a proxy-only account (step 6b), which has no record of its own, doesn't say "not your own". Supporting tests, not
// numbered test cases.
public class AnsweredNoticeTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Filing_results_puts_the_focus_on_the_saved_notice_not_the_acknowledge_button()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);

        using var filed = await FileAsync(apps, session.Client, patient, order, (Hemoglobin, "13.1"), (Platelets, "250"), (Hematocrit, "41"), (Leukocytes, "6.2"));
        var page = await ReadAsync(filed);
        Assert.Matches("<p class=\"sc-notice sc-notice--success\" role=\"status\" tabindex=\"-1\" data-focus-on-load><span class=\"sc-notice__state\">Saved.</span>", page);
        Assert.Contains("data-acknowledge", page);
        Assert.DoesNotMatch("data-acknowledge[^>]*autofocus", page);

        // Opened again, from the inbox or the chart, the button has the focus, as before.
        var opened = await session.Client.GetStringAsync(Path(patient, order), Cancel);
        Assert.DoesNotContain("data-focus-on-load", opened);
        Assert.Matches("data-acknowledge[^>]*autofocus", opened);
    }

    [Fact]
    public async Task Recording_a_proxy_puts_the_focus_on_the_saved_notice_not_the_email_field()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var page = $"/patients/{patient}/proxies";
        Assert.Matches("<input id=\"proxy-email\"[^>]*autofocus", await session.Client.GetStringAsync(page, Cancel));

        using var recorded = await PostFormAsync(session.Client, page, page, ("Email", $"synthetic-{Guid.NewGuid():N}@example.com"), ("Scope", "record"), ("Days", 90));
        var html = await ReadAsync(recorded);
        Assert.Contains("done=recorded", recorded.RequestMessage!.RequestUri!.Query);
        Assert.Matches("tabindex=\"-1\" data-focus-on-load><span class=\"sc-notice__state\">Saved:</span>", html);
        Assert.DoesNotMatch("<input id=\"proxy-email\"[^>]*autofocus", html);
    }

    [Fact]
    public async Task A_proxy_only_accounts_band_names_the_record_without_calling_it_not_their_own()
    {
        var patient = await PersonAsync(apps, "Synthetic", "Banded");
        var carer = $"synthetic-carer-{Guid.NewGuid():N}@example.com";
        await GiveAsync(patient, carer);
        await AcceptAsync(apps, InvitationPath(apps, carer), "Synthetic Band Carer");
        var session = await SignInAsync(apps, carer);

        using var chosen = await PostFormAsync(session.Client, "/portal/acting", "/portal/choose", ("grantId", await GrantIdAsync(apps, patient.Patient)));
        var home = Words(await ReadAsync(chosen));
        Assert.Contains("Viewing as a proxy: this is Synthetic Banded's record.", home);
        Assert.DoesNotContain("not your own", home);
    }
}
