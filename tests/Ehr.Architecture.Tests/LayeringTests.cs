using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Ehr.Data;
using Ehr.Domain.Phi;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Ehr.Architecture.Tests;

// Each layer calls only the one below it: the web host uses the domain, the domain uses the data layer, and nothing
// looks up. A type that did would also be a type the lower layer's tests can't cover. Within the web host, the portal
// uses none of the APIs' code. The simulated lab stands outside, as a lab's system does.
public class LayeringTests
{
    static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(Ehr.Web.Site).Assembly, typeof(PhiReader).Assembly, typeof(EhrDbContext).Assembly)
        .Build();

    [Fact]
    public void The_data_layer_knows_nothing_above_it() =>
        Types().That().ResideInAssembly(typeof(EhrDbContext).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(PhiReader).Assembly, typeof(Ehr.Web.Site).Assembly))
            .Check(Architecture);

    [Fact]
    public void The_domain_knows_nothing_of_the_web_host() =>
        Types().That().ResideInAssembly(typeof(PhiReader).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(Ehr.Web.Site).Assembly))
            .Check(Architecture);

    // The portal hands a patient FHIR through the shared mapping (Ehr.Web.Fhir), never through the clinician API's code,
    // whose door is the clinician's (the expansion plan's step 13b): one mapping, two doors. Its limit: ArchUnitNET doesn't
    // see a dependency only inside an async method's body, which the compiler moves to a type of its own.
    [Fact]
    public void The_portal_uses_none_of_the_apis_code() =>
        Types().That().ResideInNamespaceMatching(@"^Ehr\.Web\.Areas\.Portal(\..+)?$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Ehr\.Web\.Api(\..+)?$"))
            .Check(Architecture);

    // The simulated lab (the expansion plan's step 12) is a lab's system: it reaches the app over HTTP only, as a real
    // one would, and shares only the contract's source file, compiled into it. So it uses none of the app's layers.
    [Fact]
    public void The_simulated_lab_knows_nothing_of_the_app() =>
        Assert.DoesNotContain(typeof(Ehr.Lab.Simulator.LabSimulator).Assembly.GetReferencedAssemblies(),
            reference => reference.Name is "Ehr.Web" or "Ehr.Domain" or "Ehr.Data");
}
