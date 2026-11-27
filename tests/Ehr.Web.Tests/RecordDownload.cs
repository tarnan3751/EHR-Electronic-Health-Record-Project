using System.Net;
using System.Text.RegularExpressions;
using Xunit;
using static Ehr.Web.Tests.Browser;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests;

// "Download my record" (the expansion plan's step 13b): what the test cases TC-UC6-24 to 34 share. A patient whose record
// holds one of each thing the copy carries, signed in to the portal; the file and the printable page as a browser gets
// them; and a way to compare two copies that differ only in what every copy has of its own.
static partial class RecordDownload
{
    public const string PagePath = "/portal/record";

    public const string FilePath = "/portal/record?handler=File";

    public const string PrintPath = "/portal/record/print";

    // The record of FhirRequests.ChartAsync (two allergies; lisinopril, and sertraline marked sensitive; hypertension, and
    // generalized anxiety disorder, sensitive by its code; two released results; a set of vital signs; one appointment
    // booked and one cancelled with a reason), with a confirmed portal account of the patient's own, signed in.
    public static async Task<(FhirRequests.Chart Chart, ProxyTests.Person Patient)> PortalChartAsync(WebApps apps, string clinicianName)
    {
        var chart = await FhirRequests.ChartAsync(apps, clinicianName);
        var (email, _) = await apps.CreatePatientAccountAsync(chart.Patient);
        var (client, jar, path, _) = await ProxyTests.SignInAsync(apps, email);
        Assert.Equal("/portal", path);
        return (chart, new ProxyTests.Person(chart.Patient, await ProxyTests.AccountIdAsync(apps, email), email, client, jar));
    }

    // The file as the browser is given it.
    public sealed record File(HttpStatusCode Status, string? ContentType, string? Disposition, string? FileName, string? CacheControl, IReadOnlyList<string> Headers, string Body);

    public static async Task<File> DownloadAsync(HttpClient client, bool sensitive = false, string path = FilePath)
    {
        using var response = await client.GetAsync(path + (sensitive ? "&sensitive=true" : ""), Cancel);
        return new File(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentDisposition?.DispositionType,
            response.Content.Headers.ContentDisposition?.FileName,
            response.Headers.CacheControl?.ToString(),
            [.. response.Headers.Select(header => header.Key), .. response.Content.Headers.Select(header => header.Key)],
            await response.Content.ReadAsStringAsync(Cancel));
    }

    // The file's bundle: valid R4 with the profiles its entries claim, a collection put together at a moment, with no
    // search's total or links, and every entry at the urn:uuid: address of its own id.
    public static R4.Bundle Bundle(File file)
    {
        Assert.Equal(HttpStatusCode.OK, file.Status);
        var bundle = FhirConformance.Valid<R4.Bundle>(file.Body);
        Assert.Equal(R4.Bundle.BundleType.Collection, bundle.Type);
        Assert.NotNull(bundle.Timestamp);
        Assert.Null(bundle.Total);
        Assert.Empty(bundle.Link);
        Assert.All(bundle.Entry.Where(entry => entry.Resource is not R4.OperationOutcome), entry => Assert.Equal($"urn:uuid:{entry.Resource?.Id}", entry.FullUrl));
        return bundle;
    }

    public static List<T> Entries<T>(R4.Bundle bundle) where T : R4.Resource => [.. bundle.Entry.Select(entry => entry.Resource).OfType<T>()];

    // What the file says beside its entries: the OperationOutcomes' words.
    public static List<string> Notes(R4.Bundle bundle) => [.. Entries<R4.OperationOutcome>(bundle).SelectMany(outcome => outcome.Issue).Select(issue => issue.Diagnostics ?? "")];

    public static Task<string> PrintAsync(HttpClient client, bool sensitive = false) =>
        client.GetStringAsync(PrintPath + (sensitive ? "?sensitive=true" : ""), Cancel);

    // A copy without what every copy has of its own: the ids made for a file, the moment it was put together, the time a
    // page was made, and form tokens.
    public static string Comparable(string copy) => FormToken().Replace(AsOf().Replace(Timestamp().Replace(Uuid().Replace(copy, "<id>"), "<timestamp>"), "<as of>"), "<token>");

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    public static partial Regex Uuid();

    [GeneratedRegex("\"timestamp\":\"[^\"]*\"")]
    private static partial Regex Timestamp();

    [GeneratedRegex("as of [A-Z][a-z]{2} \\d{1,2}, \\d{4} \\d{2}:\\d{2} UTC")]
    private static partial Regex AsOf();

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"[^\"]*\"")]
    private static partial Regex FormToken();
}
