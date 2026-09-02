namespace Nvm.Sparkplug;

/// <summary>Ném ra khi một payload tham chiếu tới một metric bằng alias mà không birth nào từng khai báo.</summary>
/// <remarks>
/// <para>
/// Có type riêng vì câu trả lời rất cụ thể: yêu cầu edge node <b>rebirth</b> (C11), rồi decode lại.
/// Mọi lỗi decode khác đều là payload hỏng; còn cái này là một payload có lẽ vẫn ổn nhưng listener
/// bắt đầu lắng nghe quá muộn — sau một lần reconnect, sau một lần deploy, sau khi node restart và
/// đánh số lại toàn bộ.
/// </para>
/// <para>
/// Lựa chọn thay cho việc throw là đoán, và đoán ở đây tốn kém theo kiểu ẩn mình. Một alias mang
/// nghĩa theo đúng những gì birth <i>hiện tại</i> nói; một node restart có thể gán 17 cho temperature
/// dù một giờ trước đã gán nó cho voltage. Tiếp tục dùng bảng cũ sẽ ghi 3,7 vào cột temperature và
/// 31,5 vào cột voltage — đúng hình dạng, sai ý nghĩa, không có lỗi nào cả. Không gì bắt được chuyện
/// này cho tới khi một process engineer nhìn vào biểu đồ và thấy một cell đang chạy ở 3,7 °C.
/// </para>
/// </remarks>
public sealed class UnknownMetricAliasException : SparkplugDecodeException
{
    /// <summary>Tạo exception cho một alias cụ thể.</summary>
    /// <param name="alias">Alias mà payload đã dùng.</param>
    /// <param name="knownAliasCount">Bảng đã có bao nhiêu alias.</param>
    public UnknownMetricAliasException(ulong alias, int knownAliasCount)
        : base(
            $"Metric alias {alias} is not in the alias table, which holds {knownAliasCount} "
            + "alias(es). The birth that declared it was never seen, or the node has renumbered "
            + "since. Request a rebirth rather than guessing.")
    {
        Alias = alias;
        KnownAliasCount = knownAliasCount;
    }

    /// <summary>Tạo exception với một message mô tả alias không giải quyết được.</summary>
    public UnknownMetricAliasException(string message)
        : base(message)
    {
    }

    /// <summary>Tạo exception với một message và lỗi gốc bên dưới.</summary>
    public UnknownMetricAliasException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Tạo exception không có message.</summary>
    public UnknownMetricAliasException()
        : base("A metric alias is not in the alias table.")
    {
    }

    /// <summary>Alias không giải quyết được, khi exception nêu tên một alias cụ thể.</summary>
    public ulong? Alias { get; }

    /// <summary>Bảng đã có bao nhiêu alias tại thời điểm đó, khi exception nêu rõ con số này.</summary>
    /// <remarks>
    /// Zero là case đáng tách riêng khi đọc log: nó nghĩa là chưa thấy birth nào cả, chứ không phải
    /// chỉ một metric này bị lọt qua.
    /// </remarks>
    public int? KnownAliasCount { get; }
}
