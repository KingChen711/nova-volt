using System.Net;

namespace Nvm.IntegrationTests;

/// <summary>
/// Bằng chứng RED/GREEN theo ADR-034 cho C03: hai entity set mới phải trả 200 qua cùng
/// registration đã chứng minh ở C02. Test này cố tình chỉ dùng <see cref="PomEquipmentFixture"/>
/// và HTTP thô — không tham chiếu type/table mới — nên nó compile trên parent C02 và đỏ vì
/// route chưa tồn tại (404 vs 200), không phải vì thiếu type hay thiếu bảng.
/// </summary>
public sealed class PomNewRoutesTests : IClassFixture<PomEquipmentFixture>
{
    private readonly PomEquipmentFixture _fixture;

    public PomNewRoutesTests(PomEquipmentFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("ProductionUnits")]
    [InlineData("WipBoard")]
    public async Task NewReadModelsAnswerAtTheirRoute(string entitySet)
    {
        using var response = await _fixture.SendAsync(entitySet, _fixture.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
