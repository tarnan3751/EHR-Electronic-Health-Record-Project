using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.OrderTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-06
// Purpose / scenario: Placing an order is keyboard-first: a shortcut opens the form, the cursor starts in the first
//   field, every control is a labelled field or a real button or link, and the order can be signed without a mouse.
// Type: Non-functional (usability: keyboard-first)
// Requirements: NFR-12
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app and the patient is on their panel. The clinic's server
//   is up.
// Inputs and system conditions: GET the chart and GET /patients/{id}/orders/new?type=medication, checked from the
//   HTML the server renders; then POST the form as the browser sends it on Enter, without JavaScript.
// Test data: Patient Synthetic Orderable. Order type Medication, item "Metformin 500 mg tablet" (SYN-MED-002), Dose
//   "500 mg", Route "By mouth", Frequency "Twice a day".
// Steps:
//   1. Open the chart and read the "New order" link.
//   2. Open the medication order form and read its controls.
//   3. Post the form as a plain form post.
//   4. In a browser, with the keyboard only: the walkthrough in TC_UC1_06_Nfr12_KeyboardFirst.md (manual).
// Expected result: The "New order" link has aria-keyshortcuts="Alt+Shift+O" and no accesskey. Exactly one field has
//   autofocus, the first one, Item. Every input and text box that isn't hidden has a label for it. The form's only
//   other controls are a "Sign order" submit button and links; nothing takes clicks through script. The plain post
//   is answered with a redirect to the chart, and the order is saved. Acceptance criterion: all of these hold, and
//   the manual walkthrough completes with no mouse.
// Creator: Tahseen Arnan
public partial class TC_UC1_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    static readonly (string, string)[] Metformin = [("dose", "500 mg"), ("route", "By mouth"), ("frequency", "Twice a day")];

    [Fact]
    public async Task The_order_form_is_keyboard_first()
    {
        var (_, patient, session) = await OnPanelAsync(apps);

        // The shortcut, announced to assistive technology, with no accesskey for a browser's own chord to trip over.
        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Matches("<a [^>]*data-new-order aria-keyshortcuts=\"Alt\\+Shift\\+O\"[^>]*>New order <kbd class=\"sc-kbd\" aria-hidden=\"true\">Alt\\+Shift\\+O</kbd></a>", chart);
        Assert.DoesNotContain("accesskey", chart);

        var page = await session.Client.GetStringAsync(FormPath(patient, "medication"), Cancel);
        var form = OrderForm().Match(page).Value;
        Assert.NotEmpty(form);

        // The cursor starts in the first field.
        var fields = Field().Matches(form).Select(match => match.Value).Where(field => !field.Contains("type=\"hidden\"", StringComparison.Ordinal)).ToList();
        // Item, then the medication's dose, route, frequency and duration.
        Assert.Equal(5, fields.Count);
        Assert.Contains("name=\"Item\"", fields[0]);
        Assert.Contains("autofocus", fields[0]);
        Assert.Single(fields, field => field.Contains("autofocus", StringComparison.Ordinal));

        // Every field has a label of its own.
        foreach (var field in fields)
        {
            var id = Id().Match(field).Groups[1].Value;
            Assert.NotEmpty(id);
            Assert.Matches($"<label for=\"{Regex.Escape(id)}\">\\s*\\S", form);
        }

        // Everything else that can be operated is a real button or a link, which the keyboard reaches and works.
        var button = Assert.Single(Button().Matches(form));
        Assert.Contains("type=\"submit\"", button.Value);
        Assert.Contains(">Sign order</button>", form);
        Assert.Contains($"<a href=\"/patients/{patient}\">Cancel</a>", form);
        Assert.DoesNotContain("onclick", page);
        Assert.DoesNotContain("role=\"button\"", page);
        Assert.DoesNotContain("tabindex", page);

        // Enter in a field submits the form: a plain post, as without JavaScript, signs the order.
        using var signed = await SignAsync(Client(apps.OnPrem, session.Jar, followRedirects: false), patient, "medication", "Metformin 500 mg tablet", Metformin, fetch: false);
        Assert.Equal(HttpStatusCode.Redirect, signed.StatusCode);
        Assert.Equal($"/patients/{patient}", new Uri(Base, signed.Headers.Location!).AbsolutePath);
        Assert.Equal(1, await OrdersAsync(apps, patient, "SYN-MED-002"));
    }

    [GeneratedRegex("<form [^>]*data-order-form.*?</form>", RegexOptions.Singleline)]
    private static partial Regex OrderForm();

    [GeneratedRegex("<(?:input|textarea|select)\\b[^>]*>")]
    private static partial Regex Field();

    [GeneratedRegex("\\bid=\"([^\"]+)\"")]
    private static partial Regex Id();

    [GeneratedRegex("<button\\b[^>]*>")]
    private static partial Regex Button();
}
