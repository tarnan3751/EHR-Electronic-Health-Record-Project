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
                    .Where(parameter => (IsGuid(parameter.ParameterType) && !RecordsOfTheSessionsPatient.Contains(parameter.Name!))
                        || parameter.Name!.Contains("patient", StringComparison.OrdinalIgnoreCase))
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

    // The IDs a portal handler may take: each names one record, never a patient, and the page finds it only among the
    // session's patient's own (PhiReader.ThreadAsync takes the patient from the session; row-level security returns
    // nothing else). Adding a name here is a change to who can reach what: it needs line-by-line review.
    static readonly string[] RecordsOfTheSessionsPatient = ["threadId"];

    // The exception above is no wider than it says: every handler that takes a thread reads it for the session's patient.
    [Fact]
    public void A_portal_handler_that_takes_a_thread_reads_it_for_the_sessions_patient()
    {
        var models = PortalPageModels.Where(model => model.GetMethods().Any(method => method.GetParameters().Any(p => p.Name == "threadId"))).ToList();

        Assert.NotEmpty(models);
        Assert.All(models, model => Assert.NotNull(model.GetProperty("PatientId", BindingFlags.NonPublic | BindingFlags.Instance)));
    }

    static bool IsGuid(Type type) => type == typeof(Guid) || type == typeof(Guid?);
}
