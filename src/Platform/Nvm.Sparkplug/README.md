# Nvm.Sparkplug — đọc payload của tầng thiết bị

Giải mã Sparkplug B thành `DeviceReading`. Xem
[`ADR-026`](../../../docs/adr/ADR-026-sinh-c-sharp-tu-sparkplug-proto.md) cho lý do tự sinh từ
`.proto` thay vì dùng thư viện.

Ở tầng **Platform**, không phải Functional Block: Sparkplug là định dạng dây của tầng OT, không phải
nghiệp vụ của bounded context nào, và cả `Nvm.EdgeGateway` (C08) lẫn `Nvm.Ingestion` (C12) đều cần
nó. Đặt trong một FB thì FB thứ hai phải reference FB thứ nhất — đúng thứ K8 cấm.

## Cách dùng

```csharp
// Topic nói đây là birth hay data (C03), payload thì không.
var birth = SparkplugPayload.DecodeBirth(bytes);

// Bảng alias của birth là thứ làm mọi message sau đó đọc được.
var readings = SparkplugPayload.DecodeData(nextBytes, birth.Aliases);
```

Hai hàm chứ không phải một, vì **payload không tự nói nó là loại gì**. Loại message nằm ở topic MQTT
(`spBv1.0/{group}/DBIRTH/{node}/{device}`, `scope.md` §7.1). Đoán theo hình dạng payload sẽ đúng
_gần như_ mọi lúc, và đó là tần suất tệ nhất để một phép đoán đúng.

Không có state nào ở đây. Bảng alias truyền vào và trả ra, nên vòng đời của nó — **một phiên của một
edge node** — nằm ở phía người gọi. C11 là chỗ nó thành node state khoá theo `bdSeq`.

## Topic: chỗ nối vào cây ISA-95

Payload **không mang địa chỉ**. Máy nào gửi, và message là khai báo hay cập nhật, nằm hết ở topic.

```
spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142
        └ enterprise, site, area ┘     └line┘ └── device ──┘

NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142
                          └ work cell, lấy từ model ┘
```

```csharp
var topic = SparkplugTopic.Parse(mqttTopic);              // ingestion / gateway
var path  = topic.ResolveEquipmentPath(equipmentDirectory); // null = từ chối, K3
var back  = SparkplugTopic.For(path, SparkplugMessageType.DeviceData); // simulator, C05
```

**Chiều ngược lại mất work cell.** Topic có 4 bậc nhà máy, path có tới 6. `For()` bỏ work cell đi và
chỉ `ResolveEquipmentPath()` đưa nó về được — bằng cách hỏi model. Đó là lý do
`IEquipmentDirectory` tồn tại, và lý do test round-trip phải đi qua **cả hai** nửa.

Vì sao phải hỏi model chứ không cắt chuỗi: device trên dây **có khi là work cell** (`STACK-01` treo
thẳng vào line `L1`), **có khi là equipment trong một work cell** (`FORM-01-CH-0142` nằm trong
`FORM-01`). Topic không có manh mối nào phân biệt.

`ResolveEquipmentPath` trả **null** — không ném — khi nhà máy không có chỗ đó: site chưa activate,
line đã tháo, device thuộc revision chưa rollout tới. Đây là chỗ **K3** được ép, và câu trả lời là
một quyết định định tuyến chứ không phải một sự cố: ingestion từ chối message đó, nói ra topic nào,
rồi đi tiếp.

> [!warning] HOA/thường: cùng bẫy của M1/C02.1
> Cùng những cỗ máy đó còn được địa chỉ hoá bởi cây **Unified Namespace** viết thường —
> `novavolt/nv1/formation/f1/form-01/ch-0142`. Nhận cả hai cách viết là cho một cycler **hai danh
> tính**, và mọi phép đếm phía sau bị chia đôi dữ liệu. `EquipmentPath` đã từ chối chữ thường; parse
> đi qua nó nên chỗ này không có ý kiến thứ hai.

`spBv1.0/STATE/{host}` là hình dạng khác hẳn — nó gọi tên một SCADA host, không phải một chỗ trong
nhà máy. `TryParse` từ chối nó, và `IsHostState()` cho gateway phân biệt *"không gửi cho mình"* với
*"hỏng"*. Gộp hai thứ đó lại là dạy mọi người bỏ qua cảnh báo.

## Bảng alias: vì sao alias lạ phải ném

Một máy formation có 1.000 kênh, mỗi kênh 6–8 metric. Gửi `Formation/Voltage` đầy đủ trong **mọi**
`DDATA` là gửi vài trăm byte tên cho bốn byte dữ liệu, 5.000 lần một giây, qua đường truyền hẹp nhất
trong nhà máy. Sparkplug trả lời bằng **alias**: birth khai một lần *"metric 1 là Formation/Voltage,
kiểu Float"*, sau đó chỉ gửi `1`.

Nên bảng alias **không phải cache**. Nó là bản sao duy nhất của ý nghĩa mọi message sau đó, và nó chỉ
có giá trị **trong một phiên**. Node khởi động lại là được quyền gán `1` cho thứ khác.

| Nếu gặp alias lạ mà… | Chuyện gì xảy ra |
|---|---|
| **bỏ qua metric** | Danh sách rỗng — không phân biệt được với report-by-exception báo *"không có gì đổi"*. Ingestion ghi nhận một kênh khoẻ mạnh không có số liệu |
| **dùng bảng cũ** | Điện áp ghi vào cột nhiệt độ. **Đúng hình dạng, sai ý nghĩa**, không lỗi ở đâu. Chỉ lộ ra khi kỹ sư quy trình nhìn biểu đồ thấy cell chạy ở 3,7 °C |
| **ném** (đang làm) | `UnknownMetricAliasException`, mang theo `Alias` và `KnownAliasCount`. C11 trả lời bằng một **rebirth** |

Cùng bài học M1 đã học hai lần ở `CloudEventContextExtensions`: *không đọc được* khác *không có*.

## Chỗ này cố ý khó tính

| Từ chối | Vì sao |
|---|---|
| Birth có metric **không tên** | Birth là chỗ duy nhất con số có được ý nghĩa. Bỏ trống là metric đó vĩnh viễn không đọc được |
| Birth có metric **không khai datatype** | `int_value` chở cả `Int32` lẫn `UInt32`, nên `-1` và `4294967295` là **cùng một chuỗi byte**. Không khai là để cả phiên mập mờ |
| Birth gán **một alias cho hai metric** | Cái nào đọc sau thì thắng, và mọi message dùng alias đó bị quy về nhầm tín hiệu |
| Metric **không tên cũng không alias** | Không có gì để gán giá trị vào |
| Metric **không giá trị cũng không `is_null`** | Hai mệnh đề khác nhau, và payload này không phát biểu cái nào |
| **Datatype khai ≠ kiểu trên dây** | Lấy bên nào cũng là đoán xem bên kia sai, và phép đoán đó được ghi lại như một phép đo |
| Alias tới kèm **tên khác** tên birth đã gán *(→ `UnknownMetricAliasException`)* | Node đã renumber mà không báo. Chính reading này thì gán đúng — nó tự xưng tên — nhưng message alias-only **tiếp theo** sẽ đi vào metric cũ. Rebirth, đừng giữ một bảng đã sai |
| `DataSet`, `Template`, `Bytes`, `File`, array | Kênh formation không phát chúng. Gặp một cái nghĩa là payload không phải thứ pipeline này tưởng |

Mỗi mục là một `SparkplugDecodeException` — message level, không phải process level. Ingestion đẩy
payload sang chỗ đọc được kèm lý do (`_error` trên bus, `rejected/` với file drop) rồi đi tiếp. Một
payload hỏng của một kênh **không được** làm dừng 999 kênh kia.

## `MetricValue` — năm trường hợp

| Case | Từ datatype nào | Ghi chú |
|---|---|---|
| `Real(double)` | `Float`, `Double` | `Float` được nới rộng ra double vì `ts.process_signal.value` là `DOUBLE PRECISION` (`scope.md` §8.3), và nới rộng thì chính xác — thu hẹp lại thì không |
| `Integral(long)` | `Int8`…`Int64`, `UInt8`…`UInt64` | Dấu lấy từ **datatype đã khai**, không lấy từ dây |
| `Flag(bool)` | `Boolean` | |
| `Text(string)` | `String`, `Text`, `UUID` | Không phải metric nào cũng là process signal — `Formation/CellSerial` là một liên kết, C12 quyết nó đi đâu |
| `Absent` | metric có `is_null` | Thermocouple tuột ra báo `is_null`. Ghi thành `0 °C` là đưa một con số **hợp lý** vào hồ sơ truy vết; bỏ metric đi là làm nó trông như report-by-exception. Cả hai biến một *ẩn số đã biết* thành một *sự thật* |

`UInt64` lớn hơn `long.MaxValue` bị **từ chối**, không bọc vòng thành số âm — một số âm ở đây không
chỉ sai, nó còn hợp lý.

## Timestamp

`DeviceReading.DeviceTimestamp` là **đồng hồ của thiết bị**, `DateTimeOffset` (K2). Không phải lúc
gateway nhận, không phải lúc hệ thống ghi — `scope.md` §7.3 giữ ba thứ đó tách nhau vì cái đầu tiên
là cái thường xuyên sai hàng giờ, và C13 phải nói ra được điều đó.

Thứ tự lấy: **timestamp của metric** → timestamp của payload → ném. Một message gom nhiều lần đo ở
những thời điểm khác nhau — đó là lý do metric có timestamp riêng — nên gộp chúng về giờ của payload
là căn thẳng hàng những mẫu chưa bao giờ đồng thời.

> [!note] Ném khi không có timestamp nào — điểm cần C13 xác nhận lại
> `device_timestamp` nằm trong natural key (`scope.md` §7.2), nên một reading không có nó **không bao
> giờ** dedup được. Vì vậy ở đây nó là lỗi.
>
> C13 lại yêu cầu *"thiếu `device_timestamp` → `clock_quality = Unknown`, không ném"*. Hai điều này
> chỉ sống chung được nếu `Unknown` đến từ đường **file drop** (C15), nơi thật sự không có đồng hồ
> thiết bị. Đọc lại dòng này ở C13.

## Không thuộc project này

`bdSeq`, phát hiện `seq` nhảy cóc, gửi rebirth, vứt bảng alias khi node đổi phiên — tất cả là **C11**.
Đó là câu trả lời cho *"máy nào đang sống, và tôi đã bỏ lỡ gì"*, và `ADR-026` chọn tự viết chính vì
phần đó là nghiệp vụ đáng học, không phải hạ tầng đáng giao đi.

## Ranh giới được ép bằng máy

Type sinh từ `.proto` nằm ở namespace `Org.Eclipse.Tahu.Protobuf` và **không được ra khỏi assembly
này** — rule **A7** của [`Nvm.ArchitectureTests`](../../../tests/Architecture/Nvm.ArchitectureTests/README.md)
duyệt public surface và đỏ khi có một type như vậy trong chữ ký, kể cả lồng trong generic.

Bản thân file `.proto` bị pin bằng SHA-256 (`SparkplugPinTests`). Sửa một field number trong đó là
thay đổi mà **build không thấy** — protobuf đọc số lạ thành *unknown field* rồi đi tiếp.
