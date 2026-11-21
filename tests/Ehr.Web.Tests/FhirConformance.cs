using Firely.Fhir.Validation;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using Xunit;

namespace Ehr.Web.Tests;

// Checks what the FHIR API sends against the FHIR R4 specification (the expansion plan's step 11), offline, with the
// Firely .NET SDK, which only the tests use: the app writes its JSON itself (src/Ehr.Web/Fhir). First a strict
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
        var (resource, problems) = Check(json);
        Assert.True(problems.Count == 0, $"Not valid against R4: {string.Join("; ", problems)}\n{json}");
        return Assert.IsAssignableFrom<T>(resource);
    }

    // What is wrong with the JSON as R4, with the profiles it claims: the strict parse's failure, or every error and
    // warning the validator reports. Empty when nothing is. For a control case, which must fail.
    public static List<string> Problems(string json) => Check(json).Problems;

    static (Resource? Resource, List<string> Problems) Check(string json)
    {
        Resource resource;
        try
        {
            resource = Strict.DeserializeResource(json);
        }
        catch (DeserializationFailedException failure)
        {
            return (null, [$"Not valid R4 JSON: {failure.Message}"]);
        }

        OperationOutcome outcome;
        lock (Validating)
        {
            outcome = R4.Value.Validate(resource);
        }

        return (resource, [.. outcome.Issue
            .Where(issue => issue.Severity is OperationOutcome.IssueSeverity.Error or OperationOutcome.IssueSeverity.Fatal or OperationOutcome.IssueSeverity.Warning)
            .Select(issue => $"{issue.Severity} {issue.Code}: {issue.Details?.Text} at {string.Join(", ", issue.Expression ?? [])}")]);
    }
}
