using Xunit;

namespace Ehr.Testing;

// The token xunit cancels when the run is stopped or a test runs out of time. Test classes import it with
// `using static` instead of each declaring their own.
public static class TestRun
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;
}
