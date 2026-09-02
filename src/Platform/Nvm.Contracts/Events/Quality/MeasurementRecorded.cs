namespace Nvm.Contracts.Events.Quality;

/// <summary>Một measurement lấy trên một production unit đã được hệ thống chấp nhận.</summary>
/// <param name="EventId">
/// Định danh của lần xảy ra này, và cũng là trọng tâm của event này. Xem <see cref="IDomainEvent.EventId"/>.
/// </param>
/// <param name="OccurredAt">Khi ingestion commit reading này. Xem <see cref="IDomainEvent.OccurredAt"/>.</param>
/// <param name="SiteId">Plant mà reading này thuộc về (K3).</param>
/// <param name="EquipmentPath">Nơi nó được lấy, dưới dạng ISA-95 path đã resolve.</param>
/// <param name="UnitId">
/// Unit mà kết quả đã đánh giá thuộc về. Nullable để golden document v1 bất biến vẫn đọc được;
/// publication ingestion hiện tại yêu cầu một giá trị không rỗng.
/// </param>
/// <param name="StepCode">Process step mà equipment thực hiện.</param>
/// <param name="SignalCode">Metric, theo từ vựng riêng của plant.</param>
/// <param name="DeviceTimestamp">Khi device báo rằng nó đã lấy reading.</param>
/// <param name="GatewayTimestamp">Khi edge gateway nhận được publish mang reading này.</param>
/// <param name="ClockQuality"><c>Good</c>, <c>Drifted</c> hoặc <c>Unknown</c> (scope.md §7.3).</param>
/// <param name="ValueKind">Field giá trị nào mang reading.</param>
/// <param name="RealValue">Reading, cho một continuous signal.</param>
/// <param name="IntegerValue">Reading, cho một counted signal.</param>
/// <param name="BooleanValue">Reading, cho một flag.</param>
/// <param name="TextValue">Reading, cho một serial hay một label.</param>
/// <remarks>
/// <para>
/// <b><see cref="EventId"/> chính là <c>source_event_id</c> của scope.md §7.2</b> — UUIDv5 tất định
/// dựng từ natural key, cùng giá trị mà dòng dữ liệu mang trong <c>ingest.processed_message</c>. Nó
/// trở thành <c>ce_id</c> trên wire, chính là cái nối việc khử trùng lặp ở device với việc khử trùng
/// lặp ở command. R-M1-6 nói rằng một độ trôi giữa hai giá trị đó chỉ trở nên thấy được ở M2; đây
/// chính là điểm nối mà nó trở nên thấy được, và <c>MeasurementRecordedPublishingTests</c> là nơi đọc
/// lại cả hai rồi so sánh chúng.
/// </para>
/// <para>
/// <b>Không phải mọi reading đều trở thành một trong số này.</b> scope.md §5.5 vẽ ranh giới: một quan
/// sát liên tục là telemetry và dừng lại ở TimescaleDB, trong khi một giá trị <i>đã đánh giá</i> —
/// OCV chấm điểm một cell, capacity mà một formation cycle kết thúc ở đó — là một sự thật nghiệp vụ và
/// thuộc về bus. Ingestion hiện tại yêu cầu cả một signal code đã cấu hình lẫn một
/// <see cref="UnitId"/> không rỗng. Publish một cái như thế này cho mỗi formation sample sẽ đặt năm
/// nghìn event mỗi giây lên một bus vốn tồn tại để mang quyết định, và sẽ biến event store thành chính
/// cái time-series database nằm cạnh nó.
/// </para>
/// <para>
/// Cả ba timestamp đều đi theo, không cái nào đóng vai <see cref="OccurredAt"/>. Một consumer tính
/// production shift cần <see cref="DeviceTimestamp"/>; một consumer audit cần
/// <see cref="OccurredAt"/>; một consumer quyết định có nên tin cái nào cần
/// <see cref="ClockQuality"/>. Gộp chúng lại ở đây sẽ khiến quyết định đó không còn khả dụng ở phía
/// downstream, sau khi ingestion đã tốn công giữ chúng tách rời.
/// </para>
/// </remarks>
[EventContract("quality", "measurement-recorded")]
[EventVersion(1)]
public sealed record MeasurementRecorded(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string SiteId,
    string EquipmentPath,
    string? UnitId,
    string StepCode,
    string SignalCode,
    DateTimeOffset DeviceTimestamp,
    DateTimeOffset GatewayTimestamp,
    string ClockQuality,
    string ValueKind,
    double? RealValue,
    long? IntegerValue,
    bool? BooleanValue,
    string? TextValue) : IDomainEvent;
