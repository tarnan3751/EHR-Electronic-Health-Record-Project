using Ehr.Domain.Phi;
using Mono.Cecil;
using Xunit;

namespace Ehr.Architecture.Tests;

// CI gate from AGENTS.md: PHI reads only go through the PHI reader, which records them. Every method body in the web
// app and the domain is scanned for a read of the context's patients set, and the only type allowed to make one is the
// PHI reader. The scan is on the compiled IL, nested state machines included, because an async handler's body lives in
// a compiler-generated type that type-level analysis attributes to nobody. Raw SQL against the table is the reviewer's
// to catch; Dapper is allowed in Ehr.Data only (AGENTS.md).
public class PhiReaderTests
{
    static readonly string[] Assemblies = [typeof(Ehr.Web.Site).Assembly.Location, typeof(PhiReader).Assembly.Location];

    [Fact]
    public void Only_the_phi_reader_reads_patients()
    {
        var reads = PatientReads().ToList();

        // The reader's own reads prove the scan sees what it should.
        Assert.Contains(reads, read => read.Owner == typeof(PhiReader).FullName);

        var outside = reads.Where(read => read.Owner != typeof(PhiReader).FullName).Select(read => $"{read.Owner}: {read.Callee}").Distinct().ToList();
        Assert.True(outside.Count == 0,
            "Every PHI read goes through the PHI reader, which records it (AGENTS.md). These read patients directly: " + string.Join("; ", outside));
    }

    // Every call to the context's Patients property, or to Set<Patient>() on a context, with the outermost type the
    // calling method belongs to.
    static IEnumerable<(string Owner, string Callee)> PatientReads()
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
                        if (instruction.Operand is MethodReference callee && IsPatientRead(callee))
                        {
                            yield return (Outermost(type).FullName, callee.FullName);
                        }
                    }
                }
            }
        }
    }

    static bool IsPatientRead(MethodReference callee) =>
        (callee.DeclaringType.FullName == "Ehr.Data.EhrDbContext" && callee.Name == "get_Patients")
        || (callee is GenericInstanceMethod { Name: "Set" } set
            && set.DeclaringType.FullName.StartsWith("Microsoft.EntityFrameworkCore.DbContext", StringComparison.Ordinal)
            && set.GenericArguments.Any(argument => argument.FullName == "Ehr.Data.Patients.Patient"));

    static TypeDefinition Outermost(TypeDefinition type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }
}
