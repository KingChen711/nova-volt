using Nvm.Kernel.Identity;
using Nvm.Sparkplug;

namespace Nvm.Ingestion.FileDrop;

/// <summary>Một measurement đọc từ một dòng CSV.</summary>
/// <param name="EquipmentPath">Nơi nó được đo, resolve theo model của nhà máy.</param>
/// <param name="UnitId">Unit dưới máy, khi file có nêu tên.</param>
/// <param name="Reading">
/// Signal, giá trị và thời điểm đo, cùng hình dạng mà Sparkplug decoder tạo ra — để natural key được
/// xây bằng cùng một lời gọi và không thể trôi dạt (C15.1).
/// </param>
public sealed record FileMeasurement(
    EquipmentPath EquipmentPath,
    string? UnitId,
    DeviceReading Reading);

/// <summary>Một dòng mà reader không thể biến thành một measurement, và vì sao.</summary>
/// <param name="LineNumber">Số dòng đánh từ 1 trong file nguồn, tính cả header.</param>
/// <param name="Line">Dòng đó nguyên văn như đã được viết ra.</param>
/// <param name="Reason">Điều gì sai với nó, bằng những từ mà một operator có thể hành động theo.</param>
/// <param name="Identity">
/// Máy mà dòng đó nêu tên, khi phần đó còn đọc được trước khi nó fail, và null khi không đọc được.
/// Trường này tồn tại vì một dòng có thể fail ở giá trị của nó mà vẫn nói rõ ràng tuyệt đối nó thuộc
/// về máy nào — và một file nêu tên máy thứ hai chỉ ở những dòng như vậy trước đây đã vượt qua được
/// single-machine check, được archive dưới máy thứ nhất, và được xếp vào processed.
/// </param>
/// <remarks>
/// Dòng này được giữ nguyên văn. Một rejection mà operator không thể nhìn thấy bản gốc là một
/// rejection họ phải tái tạo lại trước khi có thể sửa nó, và tới lúc đó tester thường đã ghi đè lên
/// chính export của mình rồi.
/// </remarks>
public sealed record RejectedLine(
    int LineNumber,
    string Line,
    string Reason,
    EquipmentPath? Identity = null);

/// <summary>Một file CSV đã biến thành gì.</summary>
/// <param name="Measurements">Các dòng đã parse được.</param>
/// <param name="Rejected">Các dòng không parse được.</param>
public sealed record FileDropParseResult(
    IReadOnlyList<FileMeasurement> Measurements,
    IReadOnlyList<RejectedLine> Rejected);

/// <summary>Một file lẽ ra đã bị tiêu thụ mà không có nơi nào giữ các byte gốc của nó.</summary>
/// <param name="message">File nào, và cần cấu hình gì.</param>
/// <remarks>
/// Được throw thay vì chỉ log. Lựa chọn thay thế — cảnh báo rồi xếp export vào <c>processed</c> — là
/// cách một deployment kết thúc bằng việc giữ những measurement mà nó không thể tạo ra nguồn gốc, và
/// một measurement không thể tạo ra bản gốc thì không phải bằng chứng (C12.1, AGENTS.md K4).
/// </remarks>
public sealed class RawCurveArchiveMissingException(string message) : Exception(message);
