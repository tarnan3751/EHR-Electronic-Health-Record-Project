using Ehr.Data.Patients;
using Ehr.Domain.Phi;
using Mono.Cecil;
using Xunit;

namespace Ehr.Architecture.Tests;

// CI gate from AGENTS.md: PHI reads only go through the PHI reader, which records them. Every method body in the web
// app and the domain is scanned for a read of the context's patients set or of the rest of the chart (allergies,
// medications, diagnoses, visits, test results), through the sets or through EF Core's generic methods that take the entity as a type
// argument (Find, Entry, SqlQuery and the like), and the only type allowed to make one is the PHI reader. The scan is on the compiled
// IL, nested state machines included, because an async handler's body lives in a compiler-generated type that
// type-level analysis attributes to nobody. Raw SQL against the tables is the reviewer's to catch; Dapper is allowed in
// Ehr.Data only (AGENTS.md).
public class PhiReaderTests
{
    static readonly string[] Assemblies = [typeof(Ehr.Web.Site).Assembly.Location, typeof(PhiReader).Assembly.Location];

    // The context's sets of patient records, and the entities they hold.
    static readonly Dictionary<string, Type> PhiSets = new()
    {
        ["AiNoteReviews"] = typeof(AiNoteReview),
        ["AiFieldProposals"] = typeof(AiFieldProposal),
        ["ClinicalDrafts"] = typeof(ClinicalDraft),
        ["ClinicalNotes"] = typeof(ClinicalNote),
        ["Patients"] = typeof(Patient),
        ["AllergyIntolerances"] = typeof(AllergyIntolerance),
        ["MedicationStatements"] = typeof(MedicationStatement),
        ["Conditions"] = typeof(Condition),
        ["ChartHistory"] = typeof(ChartHistoryEntry),
        ["InboxItems"] = typeof(Ehr.Data.Inbox.InboxItem),
        ["Appointments"] = typeof(Ehr.Data.Scheduling.Appointment),
        ["MessageThreads"] = typeof(Ehr.Data.Messaging.MessageThread),
        ["Messages"] = typeof(Ehr.Data.Messaging.Message),
        ["Encounters"] = typeof(Encounter),
        ["Orders"] = typeof(Ehr.Data.Orders.Order),
        ["Observations"] = typeof(Observation),
    };

    [Fact]
    public void Only_the_phi_reader_reads_patient_records()
    {
        var reads = PhiReads().ToList();

        // The reader's own reads of every set prove the scan sees what it should.
        foreach (var set in PhiSets.Keys)
        {
            Assert.Contains(reads, read => read.Owner == typeof(PhiReader).FullName && read.Callee.Contains($"::get_{set}()", StringComparison.Ordinal));
        }

        var outside = reads.Where(read => read.Owner != typeof(PhiReader).FullName).Select(read => $"{read.Owner}: {read.Callee}").Distinct().ToList();
        Assert.True(outside.Count == 0,
            "Every PHI read goes through the PHI reader, which records it (AGENTS.md). These read patient records directly: " + string.Join("; ", outside));
    }

    // Every call to one of the context's PHI sets, or to a generic method of EF Core's that reads an entity it is given
    // as a type argument (ReadingTypes) for one of their entities, with the outermost type the calling method belongs to.
    static IEnumerable<(string Owner, string Callee)> PhiReads()
    {
        foreach (var path in Assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(method => method.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference callee && IsPhiRead(callee))
                        {
                            yield return (Outermost(type).FullName, callee.FullName);
                        }
                    }
                }
            }
        }
    }

    // The types whose generic methods read the entity in their type argument: the context's Set<T>, Find<T>,
    // FindAsync<T> and Entry<T>, and raw SQL's SqlQuery<T> on its Database.
    static readonly string[] ReadingTypes =
    [
        "Microsoft.EntityFrameworkCore.DbContext",
        "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade",
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions",
    ];

    static bool IsPhiRead(MethodReference callee) =>
        (callee.DeclaringType.FullName == "Ehr.Data.EhrDbContext" && PhiSets.Keys.Any(set => callee.Name == $"get_{set}"))
        || (callee is GenericInstanceMethod generic
            && ReadingTypes.Contains(generic.DeclaringType.FullName)
            && generic.GenericArguments.Any(argument => PhiSets.Values.Any(entity => argument.FullName == entity.FullName)));

    static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }
}
