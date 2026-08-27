using MassTransit;

namespace Nvm.UnitTests.Bus;

public sealed class MassTransitPinTests
{
    [Fact]
    public void MassTransit_StaysOnTheMajorVersionThatIsStillOpenSource()
    {
        // ADR-021, enforced rather than written down. MassTransit 9 moved to a commercial licence
        // (massient.com/license); version 8 remains Apache-2.0. A routine "update all packages" would
        // otherwise pull v9 in silently, and nothing about the code would look different — the
        // difference only shows up in a licence audit.
        //
        // If this test is red, do not bump the number. Read ADR-021 first, and decide deliberately.
        var version = typeof(IBus).Assembly.GetName().Version;

        version.ShouldNotBeNull();
        version.Major.ShouldBe(8, "MassTransit 9 and later require a paid licence — see docs/adr/ADR-021");
    }

    [Fact]
    public void MassTransit_RunsOnTheTargetFrameworkThisRepositoryBuilds()
    {
        // The other half of the pin. Staying on v8 is only viable while v8 still ships a net10.0
        // build; the day it does not, the licence decision has to be reopened rather than worked
        // around. Touching a MassTransit type from a net10.0 assembly is what proves it today.
        typeof(IBus).Assembly.GetName().Name.ShouldBe("MassTransit.Abstractions");
    }
}
