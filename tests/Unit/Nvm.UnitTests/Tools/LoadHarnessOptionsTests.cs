using Nvm.LoadHarness;

namespace Nvm.UnitTests.Tools;

public sealed class LoadHarnessOptionsTests
{
    [Fact]
    public void Defaults_AreTheNumbersD2IsStatedIn()
    {
        var options = new LoadHarnessOptions();

        options.Rate.ShouldBe(5_000);
        options.Duration.ShouldBe(TimeSpan.FromMinutes(10));
        options.MaxInFlightPublishes.ShouldBe(256);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveRate_IsRefused(int rate)
    {
        var options = new LoadHarnessOptions { Rate = rate };

        Should.Throw<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void AZeroDuration_IsRefused()
    {
        var options = new LoadHarnessOptions { Duration = TimeSpan.Zero };

        Should.Throw<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void AZeroInFlightWindow_IsRefused()
    {
        var options = new LoadHarnessOptions { MaxInFlightPublishes = 0 };

        Should.Throw<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void AnEmptyBrokerHost_IsRefused()
    {
        // The harness must be pointed at the EMQX service name on ot-net. An empty host would fall
        // back to whatever a default resolves to, and the measurement would be of a path production
        // does not have (R-M2-2).
        var options = new LoadHarnessOptions { BrokerHost = " " };

        Should.Throw<ArgumentException>(options.Validate);
    }
}
