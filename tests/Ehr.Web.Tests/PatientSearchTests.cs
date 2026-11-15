using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The patient search (Areas/Clinical/Pages/Patients/Index, PhiReader.SearchPatientsAsync): a name, a date of birth and
// an MRN, combined; criteria that can't be searched say why; results mark the clinician's own patients and the charts
// that ask for a reason first; every patient listed is one recorded read; and the criteria travel to the chart and back.
public partial class PatientSearchTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_name_matches_the_start_of_a_given_or_family_name_in_any_case()
    {
        var family = Unique("Quillon");
        var given = Unique("Marisol");
        var byFamily = await apps.InsertPatientAsync("Ada", family);
        var byGiven = await apps.InsertPatientAsync(given, "Other");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var familyHtml = await session.Client.GetStringAsync($"/patients?name={family[..9].ToLowerInvariant()}", Cancel);
        var givenHtml = await session.Client.GetStringAsync($"/patients?name={given.ToUpperInvariant()}", Cancel);
        var middleHtml = await session.Client.GetStringAsync($"/patients?name={family[1..]}", Cancel);

        Assert.Contains($"/patients/{byFamily}", familyHtml);
        Assert.Contains($"/patients/{byGiven}", givenHtml);
        Assert.DoesNotContain($"/patients/{byFamily}", middleHtml);
        Assert.Contains("No patients match.", middleHtml);
    }

    [Fact]
    public async Task A_date_of_birth_matches_that_day_only()
    {
        var family = Unique("Calendar");
        var born = await apps.InsertPatientAsync("Ada", family, new DateOnly(1971, 3, 14));
        var dayAfter = await apps.InsertPatientAsync("Bo", family, new DateOnly(1971, 3, 15));
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync("/patients?birthdate=1971-03-14", Cancel);

        Assert.Contains($"/patients/{born}", html);
        Assert.DoesNotContain($"/patients/{dayAfter}", html);
    }

    [Fact]
    public async Task An_MRN_matches_exactly_and_not_as_a_prefix()
    {
        var patient = await apps.InsertPatientAsync("Ada", Unique("Numbered"));
        var mrn = WebApps.MrnOf(patient);
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var exact = await session.Client.GetStringAsync($"/patients?mrn={mrn}", Cancel);
        var prefix = await session.Client.GetStringAsync($"/patients?mrn={mrn[..6]}", Cancel);

        Assert.Contains($"/patients/{patient}", exact);
        Assert.DoesNotContain($"/patients/{patient}", prefix);
        Assert.Contains("No patients match.", prefix);
    }

    [Fact]
    public async Task Criteria_combine_with_and()
    {
        var family = Unique("Combined");
        var first = await apps.InsertPatientAsync("Ada", family, new DateOnly(1966, 6, 1));
        var second = await apps.InsertPatientAsync("Bo", family, new DateOnly(1966, 6, 2));
        var elsewhere = await apps.InsertPatientAsync("Cy", Unique("Apart"), new DateOnly(1966, 6, 1));
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var nameAndDate = await session.Client.GetStringAsync($"/patients?name={family}&birthdate=1966-06-01", Cancel);
        var nameAndMrn = await session.Client.GetStringAsync($"/patients?name={family}&mrn={WebApps.MrnOf(second)}", Cancel);
        var dateAndMrn = await session.Client.GetStringAsync($"/patients?birthdate=1966-06-01&mrn={WebApps.MrnOf(second)}", Cancel);
        var all = await session.Client.GetStringAsync($"/patients?name={family}&birthdate=1966-06-01&mrn={WebApps.MrnOf(first)}", Cancel);

        Assert.Equal([first], Listed(nameAndDate));
        Assert.Equal([second], Listed(nameAndMrn));
        Assert.Empty(Listed(dateAndMrn));
        Assert.Contains("No patients match.", dateAndMrn);
        Assert.Equal([first], Listed(all));
        Assert.DoesNotContain($"/patients/{elsewhere}", nameAndDate);
    }

    [Fact]
    public async Task A_name_shorter_than_two_letters_asks_for_more_and_reads_nothing()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Brief", StaffRoles.Clinician);
        await apps.InsertPatientAsync("Ada", "Q" + Unique("uick"));
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync("/patients?name=Q", Cancel);

        Assert.Contains("Type at least 2 letters of the name.", html);
        Assert.DoesNotContain("Search results", html);
        Assert.DoesNotContain("My patients", html);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id));
    }

    [Theory]
    [InlineData("1980-02-30", "Enter the date of birth as a full date, such as 1980-05-02.")]
    [InlineData("02/03/1980", "Enter the date of birth as a full date, such as 1980-05-02.")]
    [InlineData("1980-02", "Enter the date of birth as a full date, such as 1980-05-02.")]
    [InlineData("2999-01-01", "That date of birth is in the future. Check the year.")]
    public async Task A_bad_or_future_date_of_birth_asks_for_a_correction(string birthdate, string message)
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync($"/patients?name=Ada&birthdate={Uri.EscapeDataString(birthdate)}", Cancel);

        Assert.Contains(message, html);
        Assert.DoesNotContain("Search results", html);
    }

    [Fact]
    public async Task An_MRN_longer_than_twenty_characters_asks_for_a_correction()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync($"/patients?mrn={new string('7', 21)}", Cancel);

        Assert.Contains("An MRN has at most 20 characters.", html);
        Assert.DoesNotContain("Search results", html);
    }

    // A NUL, which PostgreSQL refuses in text, or any other control character.
    [Theory]
    [InlineData("name=ab%00cd", "The name has a control character in it. Type it again.")]
    [InlineData("name=ab%09cd", "The name has a control character in it. Type it again.")]
    [InlineData("mrn=12%0045", "The MRN has a control character in it. Type it again.")]
    public async Task A_control_character_asks_for_a_correction(string query, string message)
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        using var response = await session.Client.GetAsync($"/patients?{query}", Cancel);

        var html = await ReadAsync(response);
        Assert.Contains(message, html);
        Assert.DoesNotContain("Search results", html);
    }

    // The name is a prefix, whatever it holds: LIKE's wildcards and its escape character match only themselves.
    [Fact]
    public async Task Wildcards_in_a_name_match_only_themselves()
    {
        var stem = Unique("Wild");
        var percent = await apps.InsertPatientAsync("Ada", stem + "%cent");
        var underscore = await apps.InsertPatientAsync("Ada", stem + "_score");
        var backslash = await apps.InsertPatientAsync("Ada", stem + @"\slash");
        var plain = await apps.InsertPatientAsync("Ada", stem + "xyz");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var byPercent = await session.Client.GetStringAsync($"/patients?name={Uri.EscapeDataString(stem + "%")}", Cancel);
        var byUnderscore = await session.Client.GetStringAsync($"/patients?name={Uri.EscapeDataString(stem + "_")}", Cancel);
        var byBackslash = await session.Client.GetStringAsync($"/patients?name={Uri.EscapeDataString(stem + @"\")}", Cancel);

        Assert.Equal([percent], Listed(byPercent));
        Assert.Equal([underscore], Listed(byUnderscore));
        Assert.Equal([backslash], Listed(byBackslash));
        Assert.DoesNotContain($"/patients/{plain}", byPercent + byUnderscore + byBackslash);
    }

    [Fact]
    public async Task A_search_with_no_matches_says_so()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync($"/patients?name={Unique("Nobody")}", Cancel);

        Assert.Contains("Search results", html);
        Assert.Contains("No patients match.", html);
    }

    [Fact]
    public async Task A_search_that_reaches_the_limit_says_to_narrow_it()
    {
        var family = Unique("Crowded");
        for (var i = 0; i <= Ehr.Web.Areas.Clinical.Pages.Patients.IndexModel.Limit; i++)
        {
            await apps.InsertPatientAsync($"Given{i:D3}", family);
        }

        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync($"/patients?name={family}", Cancel);

        Assert.Equal(Ehr.Web.Areas.Clinical.Pages.Patients.IndexModel.Limit, Listed(html).Count);
        Assert.Contains("More patients match than are listed here. Add a date of birth, or more of the name, to narrow the search.", html);
    }

    [Fact]
    public async Task Results_mark_charts_off_the_panel_as_restricted_and_the_panel_as_yours()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Marker", StaffRoles.Clinician);
        var family = Unique("Marked");
        var mine = await apps.InsertPatientAsync("Ada", family);
        var notMine = await apps.InsertPatientAsync("Bo", family);
        await apps.AssignAsync(clinician, mine);
        var session = await SignInAsync(apps.OnPrem, clinician);

        var html = await session.Client.GetStringAsync($"/patients?name={family}", Cancel);

        Assert.Contains("Yours", RowOf(html, mine));
        Assert.DoesNotContain("Restricted", RowOf(html, mine));
        Assert.Contains("Restricted", RowOf(html, notMine));
        Assert.DoesNotContain("Yours", RowOf(html, notMine));
        Assert.Contains("Not on your panel: opening the chart asks for a reason first.", html);
    }

    [Fact]
    public async Task Each_patient_listed_is_one_recorded_read()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Reader", StaffRoles.Clinician);
        var family = Unique("Listed");
        Guid[] patients = [await apps.InsertPatientAsync("Ada", family), await apps.InsertPatientAsync("Bo", family), await apps.InsertPatientAsync("Cy", family)];
        var session = await SignInAsync(apps.OnPrem, clinician);

        await session.Client.GetStringAsync($"/patients?name={family}", Cancel);

        foreach (var patient in patients)
        {
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
        }
    }

    [Fact]
    public async Task Results_link_to_charts_with_the_search_kept()
    {
        var family = Unique("Kept");
        var patient = await apps.InsertPatientAsync("Ada", family, new DateOnly(1972, 5, 6));
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var html = await session.Client.GetStringAsync($"/patients?name={family}&birthdate=1972-05-06&mrn=", Cancel);

        Assert.Contains($"href=\"/patients/{patient}?name={family}&amp;birthdate=1972-05-06\"", html);
        // And the form shows what was searched for.
        Assert.Contains($"name=\"name\" value=\"{family}\"", html);
        Assert.Contains("name=\"birthdate\" value=\"1972-05-06\"", html);
    }

    // The way back from a chart keeps the search, through the emergency-access reason and a change to the panel.
    [Fact]
    public async Task The_chart_links_back_to_the_search_it_came_from_through_emergency_access()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Wayback", StaffRoles.Clinician);
        var family = Unique("Wayback");
        var patient = await apps.InsertPatientAsync("Ada", family);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var search = $"/patients?name={family}";
        var backToSearch = $"<a href=\"{search}\">Back to search</a>";
        var chartLink = $"/patients/{patient}?name={family}";
        Assert.Contains($"href=\"{chartLink}\"", await session.Client.GetStringAsync(search, Cancel));

        // Not on their panel, so the chart asks for a reason first; that page leads back too, and posts to its own
        // address, criteria included.
        using var reasonPage = await session.Client.GetAsync(chartLink, Cancel);
        Assert.Equal($"/patients/{patient}/reason?name={family}", reasonPage.RequestMessage!.RequestUri!.PathAndQuery);
        var reasonHtml = await ReadAsync(reasonPage);
        Assert.Contains(backToSearch, reasonHtml);
        Assert.Contains("<form method=\"post\" class=\"account-form\">", reasonHtml);

        using var chart = await PostFormAsync(session.Client, $"/patients/{patient}/reason?name={family}", reasonPage, ("Reason", "Covering the night shift"));
        Assert.Equal(chartLink, chart.RequestMessage!.RequestUri!.PathAndQuery);
        var chartHtml = await ReadAsync(chart);
        Assert.Contains(backToSearch, chartHtml);

        Assert.Contains($"action=\"/patients/{patient}?name={family}&amp;handler=Assign\"", chartHtml);
        using var assigned = await PostFormAsync(session.Client, $"/patients/{patient}?name={family}&handler=Assign", chart);
        Assert.Equal(chartLink, assigned.RequestMessage!.RequestUri!.PathAndQuery);
        Assert.Contains(backToSearch, await ReadAsync(assigned));
    }

    // Off-site with the on-prem app unable to save, a search comes from the replica with the read-only notice, and its
    // reads wait in the cloud app's queue until the on-prem app takes them.
    [Fact]
    public async Task Off_site_during_an_outage_a_search_comes_from_the_replica_and_its_reads_are_queued_then_shipped()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Away", StaffRoles.Clinician);
        var family = Unique("Away");
        Guid[] patients = [await apps.InsertPatientAsync("Ada", family), await apps.InsertPatientAsync("Bo", family)];
        var session = await SignInAsync(apps.CloudPrimaryDrill, clinician, via: apps.Cloud);
        var shipper = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>();
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await shipper.ShipAsync(Cancel));

            var html = await session.Client.GetStringAsync($"/patients?name={family}", Cancel);

            Assert.Equal(patients.Order(), Listed(html).Order());
            Assert.Contains("Restricted", html);
            Assert.Contains("Read-only: the clinic's server can't be reached", html);
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            Assert.Equal(patients.Order(), queued.Where(e => e.ActorId == clinician.Id).Select(e => e.PatientId!.Value).Order());

            apps.PrimaryDown = false;
            Assert.True(await shipper.ShipAsync(Cancel));
            foreach (var patient in patients)
            {
                Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient, AuditSources.Cloud));
            }
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    // A family name no other test uses, so a search finds only this test's patients.
    static string Unique(string stem) => stem + Guid.NewGuid().ToString("N")[..8];

    // The patients a page lists, in order, by the chart links in its results.
    static List<Guid> Listed(string html) =>
        [.. ChartLink().Matches(html).Select(match => Guid.Parse(match.Groups[1].Value))];

    // The results table's row for the patient.
    static string RowOf(string html, Guid patient)
    {
        var link = html.IndexOf($"/patients/{patient}", StringComparison.Ordinal);
        Assert.True(link >= 0, "The patient isn't listed.");
        var start = html.LastIndexOf("<tr>", link, StringComparison.Ordinal);
        return html[start..html.IndexOf("</tr>", link, StringComparison.Ordinal)];
    }

    [GeneratedRegex("href=\"/patients/([0-9a-f-]{36})")]
    private static partial Regex ChartLink();
}
