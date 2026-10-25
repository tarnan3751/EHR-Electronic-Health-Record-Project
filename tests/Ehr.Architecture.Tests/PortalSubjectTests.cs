using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Xunit;

namespace Ehr.Architecture.Tests;

// AGENTS.md: portal handlers never accept a patient ID; the subject always comes from the session. No handler in the
// Portal area takes a parameter that could name a patient, and nothing bound from a request is one.
public class PortalSubjectTests
{
    static readonly IReadOnlyList<Type> PortalPageModels = [.. typeof(Ehr.Web.Site).Assembly.GetTypes()
        .Where(type => type.Namespace?.StartsWith("Ehr.Web.Areas.Portal", StringComparison.Ordinal) == true && typeof(PageModel).IsAssignableFrom(type))];

    [Fact]
    public void There_are_portal_pages_to_check() => Assert.NotEmpty(PortalPageModels);

    [Fact]
    public void Portal_handlers_take_no_patient_from_the_request()
    {
        var offending = new List<string>();
        foreach (var model in PortalPageModels)
        {
            foreach (var handler in model.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => method.Name.StartsWith("On", StringComparison.Ordinal)))
            {
                offending.AddRange(handler.GetParameters()
                    .Where(parameter => IsGuid(parameter.ParameterType) || parameter.Name!.Contains("patient", StringComparison.OrdinalIgnoreCase))
                    .Select(parameter => $"{model.Name}.{handler.Name}({parameter.Name})"));
            }

            // Bound properties: an account ID for a confirmation link is fine; a patient is not.
            offending.AddRange(model.GetProperties()
                .Where(property => property.GetCustomAttribute<BindPropertyAttribute>() is not null)
                .Where(property => property.Name.Contains("patient", StringComparison.OrdinalIgnoreCase)
                    || (IsGuid(property.PropertyType) && property.Name != "UserId"))
                .Select(property => $"{model.Name}.{property.Name}"));
        }

        Assert.True(offending.Count == 0, "Portal pages take their patient from the session, never from a request: " + string.Join(", ", offending));
    }

    static bool IsGuid(Type type) => type == typeof(Guid) || type == typeof(Guid?);
}
