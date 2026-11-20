using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using Xunit;

namespace Ehr.Web.Tests;

// Checks what the FHIR API sends against the FHIR R4 specification (the expansion plan's step 11), offline, with the
// Firely .NET SDK, which only the tests use: the app writes its JSON itself (src/Ehr.Web/Api/Fhir). First a strict
// parse, which refuses an element R4 doesn't have, a missing required one, a list or value that is empty, and a status
// code outside its list. Then Firely's validator, over R4's own definitions (the specification's package, in the test's
// output): the core profiles and their rules, required bindings to the codes R4 defines, and any profile the resource
// claims in meta.profile, such as the vital-sign profiles. It can't check LOINC, RxNorm, ICD-10-CM or UNII codes, which
// aren't in the package; units are checked as UCUM in UcumTests. Any error or warning fails.
static class FhirConformance
{
    static readonly FhirJsonDeserializer Strict = new(new DeserializerSettings().UsingMode(DeserializationMode.Strict));

    // Built once: the first validation of each type reads its definitions from the package, a second or so.
    static readonly Lazy<Validator> R4 = new(() =>
    {
        var definitions = new CachedResolver(ZipSource.CreateValidationSource());
        return new Validator(definitions, LocalTerminologyService.CreateDefaultForCore(definitions));
    });

    static readonly Lock Validating = new();

    // The resource the JSON holds, parsed strictly and valid against R4 and the profiles it claims.
    public static T Valid<T>(string json) where T : Resource
    {
        Resource resource;
        try
        {
            resource = Strict.DeserializeResource(json);
        }
        catch (DeserializationFailedException failure)
        {
            Assert.Fail($"Not valid R4 JSON: {failure.Message}\n{json}");
            throw;
        }

        OperationOutcome outcome;
        lock (Validating)
        {
            outcome = R4.Value.Validate(resource);
        }

        var issues = outcome.Issue
            .Where(issue => issue.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal or OperationOutcome.IssueSeverity.Warning)
            .Select(issue => $"{issue.Severity} {issue.Code}: {issue.Details?.Text} at {string.Join(", ", issue.Expression ?? [])}")
            .ToList();
        Assert.True(issues.Count == 0, $"Not valid against R4: {string.Join("; ", issues)}\n{json}");
        return Assert.IsAssignableFrom<T>(resource);
    }
}
