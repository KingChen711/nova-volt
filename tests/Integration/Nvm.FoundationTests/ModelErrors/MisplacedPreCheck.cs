using Nvm.Foundation;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests.ModelErrors;

/// <summary>Cố ý sai: pre-check của block master data gắn vào command vận hành. Registry phải từ chối lúc khởi động.</summary>
[PreCheck<LabMasterBlock>]
public sealed class MisplacedPreCheck : IPreCheck<CreateLabOrder>
{
    public PreCheckResponse Check(CreateLabOrder command, IReadOnlyPlatform platform) => new();
}
