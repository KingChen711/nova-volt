namespace Nvm.Sparkplug;

/// <summary>Một metric Sparkplug đã mang giá trị gì.</summary>
/// <remarks>
/// <para>
/// Một tập case đóng thay vì dùng <c>object</c>. Cách kia đọc ngắn hơn nhưng tốn hơn về sau: mọi nơi
/// tiêu thụ một reading — dedup key ở C04, telemetry insert ở C12, canonical event ở C14 — đều phải
/// kiểm tra runtime type và quyết định làm gì khi nó không phải loại nào đã lường trước. Viết theo
/// cách này, compiler chỉ hỏi câu đó một lần, đúng tại điểm có case mới được thêm vào.
/// </para>
/// <para>
/// Các case đi theo <b>wire</b>, không đi theo nhà máy. Sparkplug có hơn hai mươi datatype và chúng
/// gộp lại thành năm loại cho mục đích của ta: một đại lượng, một số nguyên, một tín hiệu hai trạng
/// thái, văn bản, và "device nói hiện không có giá trị gì". Một giá trị <i>nghĩa là gì</i> — ví dụ
/// <c>Formation/Voltage</c> là volt — thì đến từ tên metric và equipment path, không phải từ đây.
/// </para>
/// <para>
/// Các datatype mà M2 cố tình không decode: <c>DataSet</c>, <c>Template</c>, <c>Bytes</c>,
/// <c>File</c>, các array type và các property-set type. Một channel formation không phát ra chúng,
/// và việc bịa ra một cách biểu diễn cho dữ liệu mà chẳng ai tạo ra là cách một type kết thúc với một
/// case không ai giải thích được. Gặp phải một cái như vậy là lỗi, không phải bỏ qua âm thầm — xem
/// <see cref="SparkplugDecodeException"/>.
/// </para>
/// </remarks>
public abstract record MetricValue
{
    // Đóng từ bên ngoài assembly: một case thứ sáu thêm ở nơi khác vẫn compile được, và mọi switch
    // trong pipeline sẽ rơi xuống nhánh default của nó mà không ai được báo.
    private protected MetricValue()
    {
    }

    /// <summary>Một đại lượng đo được — <c>Float</c> hoặc <c>Double</c> trên wire.</summary>
    /// <param name="Value">Reading, đã widen thành double.</param>
    /// <remarks>
    /// <c>Float</c> được widen thay vì giữ ở 32 bit vì <c>ts.process_signal.value</c> là
    /// <c>DOUBLE PRECISION</c> (docs/scope.md §8.3) và việc widen này là chính xác tuyệt đối. Narrow
    /// lại về sau thì không được như vậy.
    /// </remarks>
    public sealed record Real(double Value) : MetricValue;

    /// <summary>Một số nguyên — bất kỳ datatype nào trong <c>Int8</c>…<c>Int64</c> / <c>UInt8</c>…<c>UInt64</c>.</summary>
    /// <param name="Value">Reading, mang dấu theo đúng datatype đã khai báo.</param>
    /// <remarks>
    /// Dấu (signedness) chính là cái bẫy. Sparkplug đặt <c>Int8</c>, <c>Int16</c> và <c>Int32</c> vào
    /// trường <c>int_value</c>, vốn là một <c>uint32</c> của protobuf, nên −1 được truyền đi dưới dạng
    /// 4294967295 và chỉ có datatype đã khai báo mới nói được đó là số nào trong hai số. Khai báo đó
    /// đến từ birth, và đó là lý do thứ hai vì sao một alias chưa biết thì không thể đoán bừa được.
    /// </remarks>
    public sealed record Integral(long Value) : MetricValue;

    /// <summary>Một tín hiệu hai trạng thái — một door interlock, một heater on/off.</summary>
    /// <param name="Value">Trạng thái.</param>
    public sealed record Flag(bool Value) : MetricValue;

    /// <summary>Văn bản — <c>String</c>, <c>Text</c> hoặc <c>UUID</c> trên wire.</summary>
    /// <param name="Value">Văn bản, không bao giờ null.</param>
    /// <remarks>
    /// Không phải metric nào cũng là process signal. <c>Formation/CellSerial</c> cho biết cell nào
    /// đang ở trong channel, và đó là một sự liên kết chứ không phải một phép đo — C12 quyết định nó
    /// nằm ở đâu.
    /// </remarks>
    public sealed record Text(string Value) : MetricValue;

    /// <summary>Device đặt <c>is_null</c>: hiện tại nó không có giá trị nào cho metric này.</summary>
    /// <remarks>
    /// Một case riêng biệt thay vì bỏ metric, và thay vì dùng số 0. Một thermocouple bị lỏng sẽ báo
    /// <c>is_null</c>; ghi nhận đó thành 0 °C thì đưa một con số nghe hợp lý vào bản ghi traceability,
    /// còn bỏ metric đi thì lại làm cho report-by-exception trông như đã quyết định là không có gì
    /// thay đổi. Cả hai đều sai theo cùng một hướng — chúng biến một cái chưa biết đã biết là chưa
    /// biết thành một sự thật.
    /// </remarks>
    public sealed record Absent : MetricValue
    {
        /// <summary>Instance dùng chung.</summary>
        /// <remarks>
        /// Mọi giá trị absent đều bằng nhau, và ở mức 5.000 msg/s thì việc không cấp phát là đáng làm.
        /// Record so sánh theo cấu trúc, nên caller tự tạo instance của riêng mình vẫn không phân biệt
        /// được với instance này.
        /// </remarks>
        public static Absent Instance { get; } = new();
    }
}
