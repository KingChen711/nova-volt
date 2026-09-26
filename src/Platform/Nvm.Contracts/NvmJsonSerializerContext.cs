using System.Text.Json.Serialization;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.FactoryModel;
using Nvm.Contracts.Events.Material;
using Nvm.Contracts.Events.ProductionExecution;
using Nvm.Contracts.Events.Quality;
using Nvm.Contracts.Events.Traceability;

namespace Nvm.Contracts;

/// <summary>
/// Cấu hình serializer duy nhất cho mọi thứ di chuyển giữa các service hoặc nằm lại trong event store.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated thay vì dựa trên reflection. Hai lý do quan trọng ở đây, theo thứ tự:
/// </para>
/// <list type="number">
///   <item><description>
///     Một type không thể serialize được sẽ trở thành lỗi <b>build</b> thay vì một exception ném ra
///     lúc ba giờ sáng trên đúng một code path chưa ai từng chạy qua.
///   </description></item>
///   <item><description>
///     Mọi hình dạng envelope đều phải được khai báo bằng tay ở dưới đây. Đó không phải là ma sát cần
///     tránh — đó là một checkpoint. Thêm một dòng ở đây chính là lúc để hỏi xem event có cần một
///     golden file hay không, và câu trả lời luôn luôn là có.
///   </description></item>
/// </list>
/// <para>
/// <c>PropertyNamingPolicy</c> là camelCase, đây là convention cho các field bên trong <c>data</c>.
/// Các attribute CloudEvents bao quanh nó viết thường và không có separator, và được đặt tên từng cái
/// một trên envelope, vì một naming policy sẽ render <c>specversion</c> thành <c>specVersion</c> và âm
/// thầm phát ra thứ không còn là CloudEvents nữa.
/// </para>
/// <para>
/// <c>WhenWritingNull</c> giữ cho các attribute tùy chọn chưa được set hoàn toàn không xuất hiện trong
/// document, đúng như đặc tả yêu cầu: một attribute vắng mặt nghĩa là vắng mặt, còn
/// <c>"subject": null</c> là một khẳng định rằng subject được biết là không có gì.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CloudEventEnvelope<FactoryModelRevisionActivated>))]
[JsonSerializable(typeof(CloudEventEnvelope<MeasurementRecorded>))]
[JsonSerializable(typeof(CloudEventEnvelope<DataCollectionRecorded>))]
[JsonSerializable(typeof(CloudEventEnvelope<ProductionUnitSerialized>))]
[JsonSerializable(typeof(CloudEventEnvelope<ProcessStepStarted>))]
[JsonSerializable(typeof(CloudEventEnvelope<ProcessStepCompleted>))]
[JsonSerializable(typeof(CloudEventEnvelope<UnitMeasurementRecorded>))]
[JsonSerializable(typeof(CloudEventEnvelope<DuplicateSerialDetected>))]
[JsonSerializable(typeof(CloudEventEnvelope<UnitQuarantined>))]
[JsonSerializable(typeof(CloudEventEnvelope<UnitAssembledInto>))]
[JsonSerializable(typeof(CloudEventEnvelope<UnitRemovedFrom>))]
[JsonSerializable(typeof(CloudEventEnvelope<GenealogyCorrectionRecorded>))]
[JsonSerializable(typeof(CloudEventEnvelope<MaterialLotConsumed>))]
[JsonSerializable(typeof(CloudEventEnvelope<RollCoated>))]
public sealed partial class NvmJsonSerializerContext : JsonSerializerContext;
