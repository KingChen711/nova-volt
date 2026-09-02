using System.Collections.Immutable;
using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.UnitTests.Sparkplug;

/// <summary>Toàn bộ đường đi của Phase A, từ đầu đến cuối: bytes vào, một identity ra.</summary>
public sealed class DeviceReadingIdentityTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0001");

    [Fact]
    public void DecodingTheSamePayloadTwiceGivesTheSameIdentities()
    {
        // Trường hợp at-least-once, trên bytes thật. Thiết bị không nhận được acknowledgement sẽ gửi
        // lại chính xác các bytes này; gateway xả một backlog thì gửi chúng nhiều giờ sau. Cả hai đều
        // phải rơi vào đúng identity đã có sẵn trong bảng deduplication.
        var first = KeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));
        var second = KeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        first.ShouldBe(second);
        first.Length.ShouldBe(2);
        first.ShouldBeUnique();
    }

    [Fact]
    public void TwoMetricsOfOneMessageAreTwoMeasurements()
    {
        // Voltage và temperature tới trong cùng một payload, tại cùng một thời điểm, từ cùng một
        // channel. Chúng chỉ khác nhau ở signal code — nên nếu trường đó bị bỏ khỏi key, một trong
        // hai sẽ biến mất ở bước deduplication mà không gì báo cáo lại điều đó.
        var keys = NaturalKeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData));

        keys.Select(key => key.SignalCode).ShouldBe(["Formation/Voltage", "Formation/Temperature"]);
        keys.Select(key => key.DeviceTimestamp).Distinct().Count().ShouldBe(1);
        keys.Select(key => key.SourceEventId).ShouldBeUnique();
    }

    [Fact]
    public void TheStepAndThePlantComeOutOfTheResolvedPath()
    {
        var key = NaturalKeysFrom(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        key.SiteId.ShouldBe("NV1");
        key.StepCode.ShouldBe("FORM");
        key.EquipmentPath.ShouldBe(Channel);
        key.UnitId.ShouldBeNull();
    }

    [Fact]
    public void TheSameReadingFromTwoPlantsIsTwoMeasurements()
    {
        // Multiplant, ở mức mà sai sót ít tốn kém nhất để phát hiện. Hai channel cùng code ở hai nhà
        // máy báo cùng một voltage tại cùng một thời điểm là hai sự kiện thật, và một key bỏ sót plant
        // ra ngoài sẽ chỉ lưu được một trong hai.
        var reading = Readings(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        var atHaiPhong = reading.NaturalKey(Channel);
        var atLeipzig = reading.NaturalKey(
            EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0001"));

        atHaiPhong.SourceEventId.ShouldNotBe(atLeipzig.SourceEventId);
    }

    [Fact]
    public void APlaceThatPerformsNoStepCannotHaveMeasuredAnything()
    {
        // Một line là nơi một edge node sống, không phải nơi một reading được đo. NDATA từ chính node
        // là node health, và C11 là thứ xử lý nó — đó không phải một process signal.
        var reading = Readings(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceData))[0];

        Should.Throw<ArgumentException>(() =>
            reading.NaturalKey(EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1")));
    }

    private static ImmutableArray<DeviceReading> Readings(byte[] data)
    {
        var birth = SparkplugPayload.DecodeBirth(SparkplugFixture.ReadBytes(SparkplugFixture.DeviceBirth));

        return SparkplugPayload.DecodeData(data, birth.Aliases);
    }

    private static MeasurementNaturalKey[] NaturalKeysFrom(byte[] data) =>
        [.. Readings(data).Select(reading => reading.NaturalKey(Channel))];

    private static Guid[] KeysFrom(byte[] data) =>
        [.. NaturalKeysFrom(data).Select(key => key.SourceEventId.Value)];
}
