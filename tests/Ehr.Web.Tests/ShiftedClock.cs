namespace Ehr.Web.Tests;

// The sign-in cookies' clock in the tests (AuthenticationSchemeOptions.TimeProvider, "used for testing"), and the
// desktop token's: this machine's, moved on by however much a test lets pass, so a test of a session's end (the
// expansion plan's steps 18 and 19a) can let 15 minutes or 12 hours go by at once. One per WebApps, shared by its apps;
// it starts with nothing passed.
public sealed class ShiftedClock : TimeProvider
{
    long shift;

    public TimeSpan Shift => TimeSpan.FromTicks(Interlocked.Read(ref shift));

    public void Pass(TimeSpan time) => Interlocked.Add(ref shift, time.Ticks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Shift;
}
