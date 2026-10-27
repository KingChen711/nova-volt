using Nvm.Foundation;
using Nvm.FoundationTests.LabModel;

namespace Nvm.FoundationTests.ModelErrors;

/// <summary>Cố ý sai: extension app gắn vào extension app khác. Registry phải từ chối lúc khởi động.</summary>
[App("LabExtExt", BaseApp = typeof(LabExtensionAppDefinition))]
public sealed class ExtensionOfExtension : AppDefinition
{
    protected override void Define(PomBuilder pom)
    {
    }
}
