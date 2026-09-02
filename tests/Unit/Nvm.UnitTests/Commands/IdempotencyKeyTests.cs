using Nvm.Kernel.Commands;
using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Commands;

public sealed class IdempotencyKeyTests
{
    // Natural key của một measurement, theo docs/scope.md §7.2:
    // (site_id, equipment_id, unit_id, step_code, device_timestamp, signal_code)
    private static readonly string[] OcvMeasurement =
    [
        "NV1",
        "NOVAVOLT/NV1/AGING/A1/OCV-03",
        "NV1CL16238A00123",
        "OCV2",
        "2026-08-25T03:15:40.001+00:00",
        "OCV",
    ];

    [Fact]
    public void FromNaturalKey_SameFactTwice_ProducesTheSameKey()
    {
        // Toàn bộ vấn đề nằm ở đây. Formation cycler không nhận được acknowledgement sẽ gửi lại đúng
        // measurement đó 800 ms sau, từ một process khác sau khi restart, hoặc ba ngày sau khi một
        // gateway xả backlog. Tất cả những lần đó đều phải rơi vào đúng một giá trị này.
        var first = IdempotencyKey.FromNaturalKey(OcvMeasurement);
        var second = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        second.ShouldBe(first);
    }

    [Fact]
    public void FromNaturalKey_KnownFact_ProducesAStableValueAcrossRunsAndMachines()
    {
        // Ghim cứng literal này chính là điều khiến "deterministic" có ý nghĩa vượt ra ngoài process
        // hiện tại. Nếu giá trị này từng thay đổi, mọi dòng đã có sẵn trong bảng deduplication sẽ
        // không còn khớp với traffic đến hôm nay nữa, và các duplicate sẽ bắt đầu lọt qua một cách
        // âm thầm.
        var key = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        key.Value.ShouldBe(Guid.Parse("c82fd38e-f3ec-5601-a915-1c8af2eb00a9"));
    }

    [Fact]
    public void FromNaturalKey_OneCharacterDifferent_ProducesADifferentKey()
    {
        var ocv = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        var acir = IdempotencyKey.FromNaturalKey([.. OcvMeasurement[..^1], "ACIR"]);

        acir.ShouldNotBe(ocv);
    }

    [Fact]
    public void FromNaturalKey_PartsThatWouldFlattenToTheSameString_StillProduceDifferentKeys()
    {
        // Lỗi mà một string.Join('|', parts) ngây thơ sẽ tạo ra: ["a|b","c"] và ["a","b|c"] đều
        // flatten thành "a|b|c", nên hai fact khác nhau lại suy ra cùng một key, và bước
        // deduplication sẽ vĩnh viễn drop một trong hai. Supplier lot code là free text đến từ hệ
        // thống của bên khác, nên việc có separator lọt vào bên trong một giá trị chỉ là vấn đề thời
        // gian, không phải có xảy ra hay không.
        var left = IdempotencyKey.FromNaturalKey("NV1", "ROL|004", "STACK");
        var right = IdempotencyKey.FromNaturalKey("NV1", "ROL", "004|STACK");

        right.ShouldNotBe(left);
    }

    [Fact]
    public void FromNaturalKey_DifferentNamespaces_ProduceDifferentKeys()
    {
        var inRoot = IdempotencyKey.FromNaturalKey(OcvMeasurement);

        var inOther = IdempotencyKey.FromNaturalKey(
            DeterministicGuid.CreateVersion5(IdempotencyKey.NovaVoltNamespace, "ingestion"),
            OcvMeasurement);

        inOther.ShouldNotBe(inRoot);
    }

    [Fact]
    public void NovaVoltNamespace_IsDerivedFromTheDnsNamespaceAndNotInvented()
    {
        // Ai cũng có thể tính lại giá trị này từ DNS namespace của RFC và domain name. Một GUID ngẫu
        // nhiên hard-code sẽ hoạt động y hệt nhưng không bao giờ có thể kiểm chứng được.
        var expected = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        IdempotencyKey.NovaVoltNamespace.ShouldBe(expected);
        IdempotencyKey.NovaVoltNamespace.ShouldBe(Guid.Parse("40e49ff3-f5f7-58a6-85aa-d4db02fa35ce"));
    }

    [Fact]
    public void From_EmptyGuid_IsRejected()
    {
        // Một key toàn số 0 chính là hình dạng của một phép gán bị quên. Chấp nhận nó sẽ gộp mọi
        // command bị quên gán key thành một, và chỉ lộ ra khi có tải.
        Should.Throw<ArgumentException>(() => IdempotencyKey.From(Guid.Empty));
    }

    [Fact]
    public void FromNaturalKey_NoParts_IsRejected()
    {
        Should.Throw<ArgumentException>(() => IdempotencyKey.FromNaturalKey());
    }

    [Fact]
    public void FromNaturalKey_NullPart_IsRejected()
    {
        Should.Throw<ArgumentException>(() => IdempotencyKey.FromNaturalKey("NV1", null!, "STACK"));
    }

    [Fact]
    public void FromNaturalKey_EmptyPart_IsAllowedAndDistinct()
    {
        // Một field optional bị thiếu vẫn là một phần hợp lệ của natural key, và nó không được phép
        // đụng độ với một key chỉ đơn giản là có ít field hơn.
        var withEmpty = IdempotencyKey.FromNaturalKey("NV1", "", "STACK");
        var withoutIt = IdempotencyKey.FromNaturalKey("NV1", "STACK");

        withoutIt.ShouldNotBe(withEmpty);
    }
}
