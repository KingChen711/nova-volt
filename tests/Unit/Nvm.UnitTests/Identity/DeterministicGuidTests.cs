using Nvm.Kernel.Identity;

namespace Nvm.UnitTests.Identity;

public sealed class DeterministicGuidTests
{
    [Fact]
    public void CreateVersion5_SetsTheVersionNibbleToFive()
    {
        // Quên hai dòng bit-twiddling vẫn cho ra một giá trị 128 bit deterministic deduplicate hoàn
        // hảo, nên mọi behavioural test vẫn xanh. Nhưng nó không cho ra UUID — điều đó chỉ lộ ở
        // boundary, khi cột uuid của PostgreSQL hoặc công cụ của auditor không đọc dữ liệu đã trong store.
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        // Version nằm ở nibble cao của byte 6, tức ký tự 14 của canonical form.
        value.ToString()[14].ShouldBe('5');
    }

    [Fact]
    public void CreateVersion5_SetsTheRfc4122VariantBits()
    {
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "novavolt.example");

        // Variant là 10xx nhị phân, nên nibble ở ký tự 19 là một trong 8, 9, a, b.
        value.ToString()[19].ShouldBeOneOf('8', '9', 'a', 'b');
    }

    [Theory]
    [InlineData("www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")]
    [InlineData("python.org", "886313e1-3b8a-5372-9b90-0c9aee199e5d")]
    public void CreateVersion5_MatchesTheValuesOtherImplementationsProduce(string name, string expected)
    {
        // Hai vector đã công bố cho uuid5 trên DNS namespace. Chúng xác nhận đây là RFC 4122 version 5
        // chứ không chỉ là thứ tự nhất quán với chính nó: byte order, hash input và bit masking đều phải
        // đúng đồng thời mới ra được các giá trị này.
        var value = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, name);

        value.ShouldBe(Guid.Parse(expected));
    }

    [Fact]
    public void CreateVersion5_SameInputTwice_IsIdentical()
    {
        var first = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "NV1|STACK");
        var second = DeterministicGuid.CreateVersion5(DeterministicGuid.DnsNamespace, "NV1|STACK");

        second.ShouldBe(first);
    }

    [Fact]
    public void CreateVersion7_ForComparison_IsNeverIdentical()
    {
        // Ghi lại lý do dùng version 5 thay vì version 7 mới hơn. UUID version gọi tên một algorithm,
        // không phải một generation: version 7 trộn thời điểm hiện tại vào, nên không trả lời được "đã
        // từng thấy fact này chưa". Xem docs/plans/M1-factory-model-bus.md §C04.1.
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        second.ShouldNotBe(first);
    }
}
