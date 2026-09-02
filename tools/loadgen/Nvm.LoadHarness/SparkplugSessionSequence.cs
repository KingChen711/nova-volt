namespace Nvm.LoadHarness;

/// <summary>Cấp phát chuỗi Sparkplug có thứ tự duy nhất mà một edge-node session sở hữu.</summary>
/// <remarks>
/// Owner gọi cái này từ một scheduler duy nhất. Concurrency thuộc về MQTT in-flight window, sau khi
/// message đã nhận được sequence của nó; nó không được phép tạo ra một sequence stream thứ hai.
/// </remarks>
public sealed class SparkplugSessionSequence
{
    private ulong _next;

    /// <summary>Trả về giá trị kế tiếp và tiến lên theo modulo phạm vi sequence 8-bit của Sparkplug.</summary>
    public ulong TakeNext()
    {
        var current = _next;
        _next = (current + 1) % 256;
        return current;
    }
}
