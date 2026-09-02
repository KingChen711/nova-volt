using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

/// <summary>Identity mà một measurement tự suy ra, và các cách nó có thể không làm được vậy.</summary>
/// <remarks>
/// Mọi assertion ở đây nói về một property: <b>fact khác, key khác; cùng fact, cùng key</b>. Mất nửa
/// đầu sẽ lưu hai reading thành một và cho ra số đếm thiếu. Mất nửa sau sẽ lưu một reading hai lần và
/// cho ra số đếm thừa. Cả hai đều không phát sinh error ở đâu, nên phải kiểm chứng thay vì tranh luận.
/// </remarks>
public sealed class MeasurementNaturalKeyTests
{
    private static readonly EquipmentPath Channel =
        EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142");

    private static readonly DateTimeOffset Measured =
        new(2026, 8, 28, 7, 15, 30, 500, TimeSpan.Zero);

    [Fact]
    public void TheSameReadingDerivedTwiceGivesTheSameIdentity()
    {
        Key().SourceEventId.ShouldBe(Key().SourceEventId);
    }

    [Fact]
    public void TheIdentityIsAVersionFiveGuidAndNotEmpty()
    {
        // Version 5 vì nó được suy ra từ input; version 7 trộn clock vào và sẽ cho một measurement
        // identity mới mỗi lần nó đến (ADR-010).
        var value = Key().SourceEventId.Value;

        value.ShouldNotBe(Guid.Empty);
        value.Version.ShouldBe(5);
    }

    [Fact]
    public void ChangingAnyOneFieldChangesTheIdentity()
    {
        // Sáu field, sáu assertion, vì derivation âm thầm bỏ qua một trong chúng vẫn sẽ pass mọi test
        // "cùng fact, cùng key" trong file này.
        var baseline = Key().SourceEventId;

        var variants = new[]
        {
            Key(path: EquipmentPath.Parse("NOVAVOLT/DE1/FORMATION/F1/FORM-01/FORM-01-CH-0142")),
            Key(path: EquipmentPath.Parse("NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0143")),
            Key(unitId: "NV1CL16238A00123"),
            Key(stepCode: "AGE"),
            Key(measured: Measured.AddMilliseconds(1)),
            Key(signalCode: "Formation/Current"),
        };

        variants.Select(variant => variant.SourceEventId).ShouldBeUnique();
        variants.ShouldAllBe(variant => variant.SourceEventId != baseline);
    }

    [Fact]
    public void OneInstantWrittenTwoWaysGivesOneIdentity()
    {
        // Cách sai tốn kém nhất và khó thấy nhất. NV1 chạy UTC+7, DE1 có daylight saving, nên cùng một
        // moment thực sự đến với cách viết khác nhau tùy gateway nào serialise nó. Hash cách viết thay vì
        // instant sẽ cho một measurement hai identity, rồi bước deduplication báo thành công trên row nó
        // vừa ghi lần thứ hai.
        var utc = Key(measured: new DateTimeOffset(2026, 8, 28, 7, 15, 30, 500, TimeSpan.Zero));
        var haiPhong = Key(measured: new DateTimeOffset(2026, 8, 28, 14, 15, 30, 500, TimeSpan.FromHours(7)));
        var leipzig = Key(measured: new DateTimeOffset(2026, 8, 28, 9, 15, 30, 500, TimeSpan.FromHours(2)));

        // Ba cách viết, ba offset khác nhau, một instant.
        haiPhong.DeviceTimestamp.Offset.ShouldNotBe(utc.DeviceTimestamp.Offset);
        leipzig.DeviceTimestamp.Offset.ShouldNotBe(utc.DeviceTimestamp.Offset);

        utc.SourceEventId.ShouldBe(haiPhong.SourceEventId);
        utc.SourceEventId.ShouldBe(leipzig.SourceEventId);
    }

    [Fact]
    public void SubMillisecondPrecisionIsNotRoundedAway()
    {
        // Sparkplug có resolution millisecond, nhưng CSV drop ở C15 và dữ liệu replay từ historian thì
        // không nhất thiết vậy. Format truncate sẽ gộp hai reading cách nhau 100 microsecond thành một.
        Key(measured: Measured.AddTicks(1)).SourceEventId
            .ShouldNotBe(Key(measured: Measured).SourceEventId);
    }

    [Fact]
    public void AReadingWithNoUnitAndOneWithAnEmptyUnitAreTheSameReading()
    {
        // Có chủ ý: "không có cell trong channel" và "field cell để trống" là cùng một phát biểu từ
        // device; cho chúng identity riêng sẽ lưu cùng một coater reading hai lần tùy cách viết mà
        // gateway của shift đó dùng.
        Key(unitId: null).SourceEventId.ShouldBe(Key(unitId: string.Empty).SourceEventId);
    }

    [Fact]
    public void TheKeyRemembersThePlantSeparatelyFromThePath()
    {
        // K3 yêu cầu site_id là field riêng, không phải thứ reader phải tách khỏi path.
        Key().SiteId.ShouldBe("NV1");
    }

    [Fact]
    public void APathThatNamesNoPlantCannotHaveMeasuredAnything()
    {
        Should.Throw<ArgumentException>(() => MeasurementNaturalKey.For(
            EquipmentPath.Parse("NOVAVOLT"),
            "FORM",
            "Formation/Voltage",
            Measured));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankStepOrSignalCodeIsRefused(string blank)
    {
        Should.Throw<ArgumentException>(() =>
            MeasurementNaturalKey.For(Channel, blank, "Formation/Voltage", Measured));

        Should.Throw<ArgumentException>(() =>
            MeasurementNaturalKey.For(Channel, "FORM", blank, Measured));
    }

    private static MeasurementNaturalKey Key(
        EquipmentPath? path = null,
        string stepCode = "FORM",
        string signalCode = "Formation/Voltage",
        DateTimeOffset? measured = null,
        string? unitId = null) =>
        MeasurementNaturalKey.For(
            path ?? Channel,
            stepCode,
            signalCode,
            measured ?? Measured,
            unitId);
}
