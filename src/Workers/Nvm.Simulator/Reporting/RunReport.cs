using System.Text.Json.Serialization;

namespace Nvm.Simulator.Reporting;

/// <summary>Những gì một run nói rằng nó đã tạo ra. Vế bên trái của reconciliation ở D1.</summary>
/// <param name="LinePath">Run này đại diện phát ngôn cho line nào.</param>
/// <param name="WrittenAt">Thời điểm file này được ghi lần cuối. Một giá trị cũ nghĩa là run đã chết.</param>
/// <param name="ProcessElapsed">Nhà máy đã tiến được bao xa, theo thời gian riêng của nó.</param>
/// <param name="Channels">Có bao nhiêu channel đã được mô phỏng.</param>
/// <param name="LogicalMeasurements">
/// ★ Con số phải bằng với <c>SELECT count(*)</c> trên telemetry. Là các signal mà các channel thực sự
/// đã <b>lấy</b> (took), được đếm tại nơi reading được lấy và trước khi bất kỳ thứ gì được đưa cho một
/// transport — một duplicate không cộng thêm vào nó, một đồng hồ drift không cộng thêm vào nó, và một
/// rebirth nhắc lại một reading đã được đếm rồi cũng không cộng thêm vào nó.
/// <para>
/// Cố tình không phải là "cái gì đã tới được broker". Nếu vậy thì cả hai vế của reconciliation này sẽ
/// cùng nằm sau cùng một đường truyền, và một run bị mất nửa batch cuối cùng sẽ báo cáo một vế trái
/// nhỏ hơn và một vế phải nhỏ hơn rồi tự nhận là chính xác.
/// </para>
/// </param>
/// <param name="AbandonedMeasurements">
/// Trong số đó, có bao nhiêu cái mà đường truyền chưa từng chuyển đi — một session kết thúc giữa
/// chừng một batch, hoặc một lần publish ném exception. Đây là một <b>chẩn đoán, không phải một điều
/// chỉnh</b>: những cái này vẫn nằm trong <paramref name="LogicalMeasurements"/>, nên D1 và D3 sẽ fail
/// vì chúng, và cả hai đều yêu cầu con số này phải là <b>0</b>. Trừ nó đi để phép so sánh bằng ra
/// khớp sẽ xóa mất chính điều mà reconciliation này tồn tại để phát hiện.
/// </param>
/// <param name="LogicalMessages">Message mà line đã soạn, bất kể có được gửi hay không.</param>
/// <param name="DuplicateMessages">Message được gửi lần thứ hai một cách cố ý.</param>
/// <param name="PublishedMessages">
/// Những gì broker đã xác nhận. Chỉ bằng <c>LogicalMessages + DuplicateMessages</c> trên một run mà
/// không có gì bị bỏ dở và không có gì còn đang bị giữ lại trong một đợt dropout mô phỏng — sự khác
/// biệt chính là lý do giữ ba con số này tách rời nhau thay vì suy ra một con số từ hai con số kia.
/// </param>
/// <param name="DriftedDevices">Channel có đồng hồ sai.</param>
/// <param name="Dropouts">Đường truyền đã rớt bao nhiêu lần.</param>
/// <param name="HeldHighWater">Số message nhiều nhất từng phải chờ cùng lúc để đường truyền quay lại.</param>
/// <param name="FaultsEnabled">Có bất kỳ fault nào được bật lên hay không.</param>
/// <remarks>
/// <para>
/// Các con số fault cố tình được đặt cạnh các tổng số. Một reconciliation ra khớp với
/// <c>DuplicateMessages = 0</c> không có nghĩa là deduplication hoạt động — nó có nghĩa là không có gì
/// bị deduplicate cả. Đó chính là <c>R-M2-1</c>, và cách phòng vệ rẻ nhất trước nó là đặt bằng chứng ở
/// nơi mà ai đọc con số tổng cũng không thể bỏ sót.
/// </para>
/// <para>
/// Là một file chứ không phải một metric, vì reconciliation phải đọc được sau khi run đã kết thúc và
/// process đã không còn nữa.
/// </para>
/// </remarks>
public sealed record RunReport(
    string LinePath,
    DateTimeOffset WrittenAt,
    TimeSpan ProcessElapsed,
    int Channels,
    long LogicalMeasurements,
    long AbandonedMeasurements,
    long LogicalMessages,
    long DuplicateMessages,
    long PublishedMessages,
    int DriftedDevices,
    long Dropouts,
    int HeldHighWater,
    bool FaultsEnabled);

/// <summary>Cấu hình serializer cho run report, và không gì khác.</summary>
/// <remarks>
/// Có context riêng của nó vì cùng lý do factory model seed cũng có: đây là một artefact cục bộ của
/// một lần chạy thử nghiệm, và nó thay đổi vì những lý do hoàn toàn khác với một wire contract.
/// </remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunReport))]
internal sealed partial class RunReportJsonContext : JsonSerializerContext;
