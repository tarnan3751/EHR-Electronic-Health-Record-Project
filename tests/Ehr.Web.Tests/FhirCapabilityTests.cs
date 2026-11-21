using System.Net;
using Ehr.Data.Patients;
using Hl7.Fhir.Utility;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.FhirRequests;
using R4 = Hl7.Fhir.Model;

namespace Ehr.Web.Tests;

// The FHIR API's capability statement says exactly what the API does (the expansion plan's step 11): every
// interaction and search parameter it declares works, with the profiles it lists; every other R4 resource type,
// search parameter and interaction is refused; and the API has no route it doesn't account for.
public class FhirCapabilityTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_capability_statement_and_what_the_api_serves_agree()
    {
        var chart = await ChartAsync(apps, "Dr. Fhir Capable");
        var client = chart.Session.Client;
        var capabilities = FhirConformance.Valid<R4.CapabilityStatement>(Expect(await GetAsync(client, "/fhir/metadata"), HttpStatusCode.OK).Json);
        Assert.Equal(R4.CapabilityStatementKind.Instance, capabilities.Kind);
        Assert.Equal(["json"], capabilities.Format);
        Assert.Equal("https://localhost/fhir", capabilities.Implementation?.Url);
        var rest = Assert.Single(capabilities.Rest);
        Assert.Equal(R4.CapabilityStatement.RestfulCapabilityMode.Server, rest.Mode);
        Assert.False(rest.Security?.Cors);
        Assert.Empty(rest.Interaction);

        // An entry of each type, and a value each declared search parameter takes.
        var ids = new Dictionary<string, Guid>
        {
            ["Patient"] = chart.Patient,
            ["AllergyIntolerance"] = chart.Peanut,
            ["MedicationStatement"] = chart.Lisinopril,
            ["Condition"] = chart.Hypertension,
            ["Observation"] = chart.Vitals[VitalSign.HeartRate],
            ["Appointment"] = chart.Booked,
        };
        var values = new Dictionary<string, string>
        {
            ["patient"] = chart.Patient.ToString(),
            ["identifier"] = chart.Mrn,
            ["category"] = "vital-signs",
            ["show-sensitive"] = "true",
        };
        Assert.Equal(ids.Keys.Order(StringComparer.Ordinal), rest.Resource.Select(resource => resource.Type).Order(StringComparer.Ordinal));
        foreach (var resource in rest.Resource)
        {
            var type = resource.Type;
            Assert.NotNull(type);
            Assert.Equal(["read", "search-type"], resource.Interaction.Select(interaction => interaction.Code!.Value.GetLiteral()));

            // Read.
            var read = FhirConformance.Valid<R4.Resource>(Expect(await GetAsync(client, $"/fhir/{type}/{ids[type]}"), HttpStatusCode.OK).Json);
            Assert.Equal((type, ids[type].ToString()), (read.TypeName, read.Id));

            // Search, with each parameter declared: the one a search needs alone, each other beside it.
            var needed = type == "Patient" ? "identifier" : "patient";
            Assert.Contains(resource.SearchParam, parameter => parameter.Name == needed);
            foreach (var parameter in resource.SearchParam)
            {
                var query = $"{needed}={values[needed]}" + (parameter.Name == needed ? "" : $"&{parameter.Name}={values[parameter.Name!]}");
                var (_, matches, _) = Searched<R4.Resource>(await GetAsync(client, $"/fhir/{type}?{query}"));
                Assert.NotEmpty(matches);
                Assert.All(matches, match => Assert.Equal(type, match.TypeName));
            }

            // R4's other search parameters for the type, and the common ones, are refused.
            var declared = resource.SearchParam.Select(parameter => parameter.Name).ToHashSet();
            var others = R4.ModelInfo.SearchParameters.Where(parameter => parameter.Resource == type).Select(parameter => parameter.Name)
                .Concat(["_id", "_lastUpdated", "_count", "_sort", "_include", "_revinclude", "_summary", "_elements"]).Distinct().Where(name => !declared.Contains(name)).ToList();
            Assert.NotEmpty(others);
            foreach (var other in others)
            {
                Refused(await GetAsync(client, $"/fhir/{type}?{needed}={values[needed]}&{other}=1"), HttpStatusCode.BadRequest, "not-supported");
            }

            // Interactions not declared: version reads and history, and every write.
            Refused(await GetAsync(client, $"/fhir/{type}/{ids[type]}/_history/1"), HttpStatusCode.NotFound, "not-found");
            Refused(await GetAsync(client, $"/fhir/{type}/_history"), HttpStatusCode.NotFound, "not-found");
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                Refused(await SendAsync(client, method, $"/fhir/{type}/{ids[type]}"), HttpStatusCode.MethodNotAllowed, "not-supported");
            }

            // The profiles it lists are those its entries claim: only observations claim any (the vital signs').
            if (type == "Observation")
            {
                var (_, signs, _) = Searched<R4.Observation>(await GetAsync(client, $"/fhir/Observation?patient={chart.Patient}&category=vital-signs"));
                Assert.Equal(signs.SelectMany(sign => sign.Meta!.Profile).Distinct().Order(StringComparer.Ordinal), resource.SupportedProfile.Order(StringComparer.Ordinal));
            }
            else
            {
                Assert.Empty(resource.SupportedProfile);
            }
        }

        // Every other R4 resource type: neither read nor searched.
        foreach (var type in R4.ModelInfo.SupportedResources.Where(type => !ids.ContainsKey(type)))
        {
            Refused(await GetAsync(client, $"/fhir/{type}?patient={chart.Patient}"), HttpStatusCode.NotFound, "not-supported");
            Refused(await GetAsync(client, $"/fhir/{type}/{chart.Patient}"), HttpStatusCode.NotFound, "not-supported");
        }

        // Nothing at the level of the whole system: no search, history or batch.
        Refused(await GetAsync(client, "/fhir?_type=Patient"), HttpStatusCode.NotFound, "not-found");
        Refused(await GetAsync(client, "/fhir/_history"), HttpStatusCode.NotFound, "not-supported");
        Refused(await SendAsync(client, HttpMethod.Post, "/fhir"), HttpStatusCode.MethodNotAllowed, "not-supported");

        // And the API's routes are these, and only these: the capability statement, the type, the entry, and the rest.
        var routes = apps.OnPrem.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/fhir", StringComparison.Ordinal) == true)
            .Select(endpoint => $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["any method"])} {endpoint.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal);
        Assert.Equal(["GET /fhir/metadata", "GET /fhir/{type}", "GET /fhir/{type}/{id}", "any method /fhir/{**rest}"], routes);
    }
}
