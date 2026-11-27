using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// A field whose value was refused says so to assistive technology, and points at the message that says how to fix it
// (docs/brand: TextField; Ehr.Web/TagHelpers). The reason for emergency access stands for every form built the same way.
public class FieldErrorTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_refused_field_is_marked_invalid_and_described_by_its_message()
    {
        var patient = await apps.InsertPatientAsync("Vale", "Described");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());

        var asked = await session.Client.GetStringAsync($"/patients/{patient}/reason", Cancel);
        using var refused = await PostFormAsync(session.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "ER"));
        var html = await ReadAsync(refused);

        // Before anything is wrong the field is not marked, and its message is empty.
        Assert.DoesNotContain("aria-invalid", asked);
        Assert.Matches("<span [^>]*id=\"Reason-error\"[^>]*></span>", asked);

        Assert.Matches("<textarea [^>]*id=\"Reason\"[^>]*aria-invalid=\"true\"[^>]*aria-describedby=\"Reason-error\"", html);
        Assert.Matches("<span [^>]*id=\"Reason-error\"[^>]*>Too short. Use 10 to 500 characters.</span>", html);
    }
}
