using Nvm.Ingestion.FileDrop;

namespace Nvm.UnitTests.Ingestion;

public sealed class FileDropOptionsTests
{
    [Fact]
    public void AnEnabledAdapter_CannotDisableThePublishContract()
    {
        var options = new FileDropOptions { Enabled = true, PublishedSuffix = string.Empty };

        Should.Throw<ArgumentException>(options.Validate)
            .ParamName.ShouldBe(nameof(FileDropOptions.PublishedSuffix));
    }

    [Theory]
    [InlineData(".ready")]
    [InlineData(".done")]
    public void AnEnabledAdapter_AcceptsAnExplicitPublishSuffix(string suffix)
    {
        var options = new FileDropOptions { Enabled = true, PublishedSuffix = suffix };

        Should.NotThrow(options.Validate);
    }
}
