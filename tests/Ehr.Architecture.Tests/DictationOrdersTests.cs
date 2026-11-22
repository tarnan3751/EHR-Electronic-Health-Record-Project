using Ehr.Domain.Phi;
using Mono.Cecil;
using Xunit;

namespace Ehr.Architecture.Tests;

// FR-14, for UC 1 Place an order (Tahseen Arnan): dictation never places, saves or signs an order or an allergy change.
// Since the expansion plan's step 15 (FR-14 as reworded) it may propose an order, or an allergy to add, which only the
// clinician turns into one, through the order form or the allergy form and every check of them; every other order or
// allergy change a clinician dictates is listed as not carried over, as before. So nothing in the dictation code, or in
// the review that shows what it proposes, may reach the code that signs an order or writes the allergy list; and only
// that code links a proposal to the order or allergy it became (the database holds the link to one made just then by
// the clinician deciding: db/policies/55-ai-review.sql). Every type in the web app and the domain is scanned,
// compiler-generated nested types included, by what it names in a field, a signature or a method body.
public class DictationOrdersTests
{
    static readonly string[] Assemblies = [typeof(Ehr.Web.Site).Assembly.Location, typeof(PhiReader).Assembly.Location];

    // Also TC-UC10-02 (FR-14): no dictation code can reach the order code. The primary test is the sidecar's
    // test_validation.py.
    [Fact]
    public void Nothing_in_the_dictation_code_reaches_orders()
    {
        var dictation = 0;
        var reaches = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var type in assembly.MainModule.GetTypes().Where(type => IsDictation(Outermost(type))))
            {
                dictation++;
                foreach (var used in TypesUsed(type).Where(IsOrders))
                {
                    reaches.Add($"{Outermost(type).FullName}: {used}");
                }
            }
        }

        // The scan found the dictation API and page to look through.
        Assert.True(dictation > 5, $"Only {dictation} dictation types were found to scan.");
        Assert.True(reaches.Count == 0, "Dictation never places an order (FR-14). These reach the order code: " + string.Join("; ", reaches));
    }

    // Also TC-UC10-02 (FR-14 as reworded in step 15): nothing in the dictation code, nor the review that shows and decides
    // what a dictation proposes, reaches the code that signs an order or writes the allergy list: the test above holds
    // them to the order code, this to the chart lists' writer too, which allergies go through.
    [Fact]
    public void Nothing_in_the_dictation_code_or_its_review_reaches_order_signing_or_the_chart_lists()
    {
        var scanned = 0;
        var reaches = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var type in assembly.MainModule.GetTypes().Where(type => IsDictation(Outermost(type)) || Reviews.Contains(Outermost(type).FullName)))
            {
                scanned++;
                foreach (var used in TypesUsed(type).Where(used => IsOrders(used) || used.Contains(ChartListsWriter, StringComparison.Ordinal)))
                {
                    reaches.Add($"{Outermost(type).FullName}: {used}");
                }
            }
        }

        Assert.True(scanned > 7, $"Only {scanned} dictation and review types were found to scan.");
        Assert.True(reaches.Count == 0, "Dictation never signs an order or writes the allergy list (FR-14). These reach that code: " + string.Join("; ", reaches));
    }

    // Also TC-UC10-02 (step 15): a proposal becomes ordered only where an order is signed (ClinicalOrders), and recorded
    // only where an allergy is added (ChartLists), each in the savepoint that signs or adds it; and its decision is written
    // nowhere else but there and in the review's own service (NoteReviews: staging it, dismissing it).
    [Fact]
    public void Only_signing_an_order_or_adding_an_allergy_links_a_proposal_to_it()
    {
        var writers = new Dictionary<string, SortedSet<string>>
        {
            ["set_OrderId"] = new(StringComparer.Ordinal),
            ["set_AllergyId"] = new(StringComparer.Ordinal),
            ["set_State"] = new(StringComparer.Ordinal),
        };
        foreach (var path in Assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var called in type.Methods.Where(method => method.HasBody).SelectMany(method => method.Body.Instructions)
                    .Select(instruction => instruction.Operand).OfType<MethodReference>()
                    .Where(called => called.DeclaringType.FullName == "Ehr.Data.Patients.AiChartProposal" && writers.ContainsKey(called.Name)))
                {
                    writers[called.Name].Add(Outermost(type).FullName);
                }
            }
        }

        Assert.Equal(["Ehr.Domain.Orders.ClinicalOrders"], writers["set_OrderId"]);
        Assert.Equal(["Ehr.Domain.Patients.ChartLists"], writers["set_AllergyId"]);
        Assert.Equal(["Ehr.Domain.Notes.NoteReviews", "Ehr.Domain.Orders.ClinicalOrders", "Ehr.Domain.Patients.ChartLists"], writers["set_State"]);
    }

    // The review of a dictation, and the service behind it, whose names don't say dictation.
    static readonly HashSet<string> Reviews = ["Ehr.Domain.Notes.NoteReviews", "Ehr.Web.Areas.Clinical.Pages.Patients.Notes.ReviewModel"];

    // What writes a patient's allergies, medications and diagnoses (step 2).
    const string ChartListsWriter = "Ehr.Domain.Patients.ChartLists";

    static bool IsDictation(TypeDefinition type) => type.FullName.Contains("Dictat", StringComparison.Ordinal);

    static bool IsOrders(string type) => type.Contains(".Orders.", StringComparison.Ordinal);

    // The full names of every type the type names: its base and interfaces, its fields, its methods' signatures and
    // locals, and whatever its method bodies call, read or create.
    static IEnumerable<string> TypesUsed(TypeDefinition type)
    {
        if (type.BaseType is { } parent)
        {
            yield return parent.FullName;
        }

        foreach (var implemented in type.Interfaces)
        {
            yield return implemented.InterfaceType.FullName;
        }

        foreach (var field in type.Fields)
        {
            yield return field.FieldType.FullName;
        }

        foreach (var method in type.Methods)
        {
            yield return method.ReturnType.FullName;
            foreach (var parameter in method.Parameters)
            {
                yield return parameter.ParameterType.FullName;
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (var local in method.Body.Variables)
            {
                yield return local.VariableType.FullName;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    // FullName carries the declaring type and any generic arguments.
                    case MethodReference called:
                        yield return called.FullName;
                        break;
                    case FieldReference read:
                        yield return read.FullName;
                        break;
                    case TypeReference named:
                        yield return named.FullName;
                        break;
                }
            }
        }
    }

    static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }
}
