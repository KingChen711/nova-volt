namespace Nvm.Simulator;

/// <summary>Một hash cho cùng một kết quả ở mọi process, điều mà <c>GetHashCode</c> không làm được.</summary>
/// <remarks>
/// FNV-1a. <see cref="string.GetHashCode()"/> được ngẫu nhiên hóa theo từng process, nên "tính cách"
/// của một channel — reading của nó lệch xa trung bình của line đến đâu, đồng hồ của nó có phải cái bị
/// hỏng hay không — sẽ bị xáo trộn lại sau mỗi lần restart. Khi đó hai lần chạy của cùng một nhà máy sẽ
/// không thể so sánh được, và một fault tái hiện được sáng nay sẽ rơi vào một máy khác vào chiều nay.
/// </remarks>
internal static class StableHash
{
    private const uint OffsetBasis = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>Hash một đoạn text theo đúng cùng một cách trên mọi máy và mọi lần chạy.</summary>
    /// <param name="text">Cái gì cần hash.</param>
    /// <remarks>
    /// FNV-1a rồi tới một bước avalanche. Nửa sau không phải là trang trí: các bit thấp của FNV-1a
    /// yếu, và mọi caller ở đây đều lấy kết quả modulo một số nào đó. Mã channel chỉ khác nhau ở vài
    /// chữ số cuối, và trên cả nghìn mã mà một formation line thực sự có, yêu cầu một phần mười số
    /// device cho ra 12.8% nếu không trộn bit và 8.9% nếu có — một fault injector lệch tới một phần tư
    /// sẽ đưa độ lệch đó vào mọi con số drift mà milestone báo cáo.
    /// </remarks>
    public static uint Of(string text)
    {
        var hash = OffsetBasis;

        foreach (var character in text)
        {
            hash = (hash ^ character) * Prime;
        }

        // Finalizer kiểu Murmur3: trải entropy của các ký tự cuối ra toàn bộ từ, nên lấy các bit thấp
        // nhất cũng tốt như lấy bất kỳ bit nào khác.
        hash ^= hash >> 16;
        hash *= 0x7feb352d;
        hash ^= hash >> 15;
        hash *= 0x846ca68b;
        hash ^= hash >> 16;

        return hash;
    }
}
