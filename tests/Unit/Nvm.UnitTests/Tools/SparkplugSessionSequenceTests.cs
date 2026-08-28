using Nvm.LoadHarness;

namespace Nvm.UnitTests.Tools;

public sealed class SparkplugSessionSequenceTests
{
    [Fact]
    public void DataContinuesImmediatelyAfterTheNodeAndDeviceBirths()
    {
        var sequence = new SparkplugSessionSequence();

        sequence.TakeNext().ShouldBe(0ul); // NBIRTH

        foreach (var expected in Enumerable.Range(1, 8))
        {
            sequence.TakeNext().ShouldBe((ulong)expected); // one DBIRTH per seeded channel
        }

        sequence.TakeNext().ShouldBe(9ul); // first DDATA in the same edge-node session
    }

    [Fact]
    public void SequenceWrapsFrom255ToZeroWithoutStartingANewSession()
    {
        var sequence = new SparkplugSessionSequence();

        foreach (var expected in Enumerable.Range(0, 256))
        {
            sequence.TakeNext().ShouldBe((ulong)expected);
        }

        sequence.TakeNext().ShouldBe(0ul);
    }
}
