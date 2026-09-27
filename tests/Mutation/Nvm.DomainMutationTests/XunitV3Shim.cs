namespace Xunit;

/// <summary>Shim tối thiểu của TestContext xunit v3 để chạy lại unit test domain trên xunit v2 cho Stryker.</summary>
internal sealed class TestContext
{
    public static TestContext Current { get; } = new();

#pragma warning disable CA1822 // Giữ đúng hình dạng API thành viên của xunit v3.
    public CancellationToken CancellationToken => CancellationToken.None;
#pragma warning restore CA1822
}
