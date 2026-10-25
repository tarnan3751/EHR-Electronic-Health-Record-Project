using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using Ehr.Data;
using Ehr.Domain.Phi;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Ehr.Architecture.Tests;

// Each layer calls only the one below it: the web host uses the domain, the domain uses the data layer, and nothing
// looks up. A type that did would also be a type the lower layer's tests can't cover.
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
}
