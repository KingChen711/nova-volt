# Sổ ghi số đo

Mọi con số trong repo này phải có một dòng ở đây. Không có dòng thì không được nhắc tới con số
đó ở chỗ khác.

## Luật ghi

1. **Chỉ số thật.** Không ước lượng, không "khoảng", không "tầm". Chưa đo thì để trống, đừng đoán.
2. **Điều kiện đo là bắt buộc.** Một con số không kèm điều kiện là một con số vô nghĩa —
   `make up` 48 s với image đã cache và 48 s khi phải tải image là hai chuyện khác hẳn.
3. **Một phép đo = một dòng.** Đo ba lần thì ba dòng, đừng lấy trung bình rồi ghi một dòng. Độ
   phân tán là thông tin.
4. **Đo lại thì thêm dòng mới**, không sửa dòng cũ. Số cũ cho biết thay đổi nào làm chậm đi.
5. Cột `Commit` là commit **tại thời điểm đo**, không phải commit thêm dòng này.

Máy đo: Windows 11, Docker Desktop, quota RAM 8 GB (xem `docs/plans/M0-bootstrap.md` §C09.4).

---

## M0 — Bootstrap

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-25 | `3aebc86` | Khởi động toàn bộ stack từ `down` | **58 s** | 6 service 53 s + profile `init` 5 s. Image **đã có sẵn trong cache** |
| 2026-08-25 | `3aebc86` | Tổng `mem_limit` khai báo | **6,00 GB** | 6 service của profile mặc định. Quota Docker 8 GB |
| 2026-08-25 | `3aebc86` | RAM thực dùng lúc rảnh | **~2,1 GB** | `docker stats`, không tải, không ai đăng nhập |
| 2026-08-26 | `4f6408f` | 5 readiness probe lúc bình thường | **451 ms** | Tổng thời gian cả 5 check trong một lần gọi `/health/ready` |
| 2026-08-26 | `4f6408f` | Phát hiện SQL Server chết | **3,2 s** | Tắt container `mssql` → `/health/ready` chuyển `Unhealthy`. Ngưỡng D5: < 10 s |
| 2026-08-26 | `4f6408f` | Phục hồi sau khi bật lại SQL Server | **13 s** | `Application started` chỉ xuất hiện **1 lần** → app không restart |
| 2026-08-26 | `4f6408f` | Khởi động app khi SQL Server đang tắt | **2 s** | `live=Healthy`, `ready=Unhealthy[sqlserver]`. Chứng minh ràng buộc N15 |
| 2026-08-26 | `0719cad` | `make up` lần 1 | **48 s** | Sau `make down-v` — volume rỗng, image đã cache |
| 2026-08-26 | `0719cad` | `make up` lần 2 | **36 s** | Sau `make down` — giữ volume |
| 2026-08-26 | `0719cad` | `make up` lần 3 | **42 s** | Sau `make down` — giữ volume |
| 2026-08-26 | `0719cad` | Kích thước `git bundle` backup | **153 KB** | `make backup`, `git bundle verify` trả OK |
| 2026-08-26 | `f6f8fb8` | 5 readiness probe, lời gọi **lặp lại** | **10,4 ms** rồi **21,4 ms** | Cùng endpoint như dòng 451 ms ở trên, nhưng container đã ấm và connection pool đã mở. Chênh ~40 lần → 451 ms là **giá của lần đầu**, không phải giá thường trực |

> [!note] Ba con số `make up` đo cái gì, và không đo cái gì
> 48/36/42 s là thời gian **khởi động** với image đã nằm trong cache Docker — thoải mái so với
> ngưỡng 300 s của ★D1. Chúng không bao gồm thời gian **tải image** (`mssql` một mình hơn 1 GB).
>
> ★D1 đã được phát biểu lại cho khớp: *"sau `make down-v`, image đã cache"*. Thời gian tải phụ
> thuộc đường truyền chứ không phụ thuộc repo, nên đo nó không nói lên điều gì về code. Khi nào
> có người thứ hai clone repo thì thêm một dòng với điều kiện *"máy chưa từng có image"*.

---

## M1 — Factory Model & Manufacturing Service Bus

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-27 | `4e7fc02` | ★ **Event mất khi broker chết 30 s** | **18 / 200** | `make bus-chaos`. Publish 200 event cách nhau 100 ms, timeout 2 s mỗi lần; `stop rabbitmq` ở giây thứ 5, `start` sau 30 s. **Chưa có outbox** — xem `ADR-022` |
| 2026-08-27 | `4e7fc02` | Cửa sổ mất, theo số thứ tự event | **48 → 65** | Cùng lần chạy. Một khối liền, không rải rác |
| 2026-08-27 | `4e7fc02` | Publish thành công / consumer nhận được | **182 / 182** | Cùng lần chạy. Hai số **bằng nhau**: số mất đúng bằng số publish thất bại, không có vùng xám |
| 2026-08-27 | `4e7fc02` | Thời gian chạy hết 200 event | **59,0 s** | Cùng lần chạy. Chạy trơn mất ~20 s; 39 s chênh là 18 lần × 2 s timeout |
| 2026-08-27 | `4e7fc02` | Số lần app restart trong lúc broker chết | **0** | `Application started` xuất hiện đúng 1 lần trong `host.log`. `/health/live` = `Healthy` suốt |
| 2026-08-27 | `4e7fc02` | Số check `ready` đỏ khi RabbitMQ tắt | **1 / 6** | Chỉ check `rabbitmq`. `sqlserver`, `postgres`, `keycloak`, `minio`, `masstransit-bus` vẫn xanh — lỗi không lan |
| 2026-08-27 | `4e7fc02` | Số lần thử của consumer lỗi | **5** | `make bus-dlq`. Khoảng cách đo được: 245 / 480 / 920 / 1933 ms — exponential có jitter, khớp `NvmRetryPolicy` |
| 2026-08-27 | `4e7fc02` | Message trong `_error` sau 5 lần thử | **1** | Cùng lần chạy. Queue chính còn **0** — không mất, chỉ đứng riêng |
| 2026-08-27 | `08ef83b` | **6** readiness probe, lời gọi đầu | **272,3 ms** | Cả 6 `Healthy`. M0 đo **451 ms** với 5 probe — thêm probe thứ sáu không làm chậm đi |
| 2026-08-27 | `08ef83b` | 6 readiness probe, lời gọi lặp lại | **7,9 ms** rồi **7,0 ms** | Cùng endpoint, container đã ấm. M0: 10,4 / 21,4 ms với 5 probe |
| 2026-08-27 | `08ef83b` | ★ **Khởi động app khi RabbitMQ đang tắt** | **249 ms** | App lên bình thường. `live=Healthy`; `ready=Unhealthy` ở **`bus`** (*"Not ready: not started"*, *"Broker unreachable"*) và **`rabbitmq`**; 4 probe còn lại xanh |
| 2026-08-27 | `08ef83b` | Phát hiện SQL Server chết | **3157 ms** | Regression của D5/M0 (đo 3,2 s). Ngưỡng < 10 s. Chỉ `sqlserver` đỏ, 5 probe khác không lan |
| 2026-08-27 | `08ef83b` | ★ `bus` phát hiện broker chết **sau** khi bus đã khởi động | **không phát hiện** | `Healthy` liên tục **152 s** với broker đã `docker compose stop`. Xem ghi chú bên dưới |
| 2026-08-27 | `08ef83b` | `ready` xanh lại sau khi bật lại broker — lần 1 | **19,8 s** | Broker tắt ~40 s trước đó. Con số này chủ yếu là thời gian **container RabbitMQ khởi động**, không phải thời gian app nối lại |
| 2026-08-27 | `08ef83b` | `ready` xanh lại sau khi bật lại broker — lần 2 | **4,7 s** | Broker tắt ~10 s. `check_running` của broker và `ready` của app xanh trong **cùng một nhịp poll 1 s** → phần app tự đóng góp < 1 s |
| 2026-08-27 | `08ef83b` | Số lần app restart trong cả bốn kịch bản trên | **0** | `Application started` đúng 1 lần mỗi lần chạy |
| 2026-08-27 | `2a927a9` | ★ **Test đỏ khi hoàn nguyên idempotency về check-then-act** | **6 / 292** | Lab phá hoại A của R2. Cả 6 nằm trong `IdempotencyConcurrencyTests`. **286 test còn lại xanh**, kể cả phép kiểm K7 tuần tự của C05. Cây làm việc R2, chưa commit. Xem `ADR-023` |
| 2026-08-27 | `2a927a9` | Số lần handler chạy — 8 caller một khoá, check-then-act | **7** (mong đợi 1) | Cùng lần chạy lab A, test `EightCallersOneKey_FiftyRoundsRunning_NeverHandleTwice` |
| 2026-08-27 | `2a927a9` | Số lần handler chạy — 8 caller một khoá, handler ném lỗi lần đầu, check-then-act | **8** (mong đợi 2) | Cùng lần chạy lab A, test `ConcurrentCallersWhenTheHandlerThrows_...` |
| 2026-08-27 | `2a927a9` | ★ **Test đỏ khi bỏ compare-and-swap của activation** | **2 / 292** | Lab phá hoại B của R2. `SixteenActivationsAtOnce_ExactlyOneWins`: **16/16** caller cùng thắng thay vì 1. **Không test nào ở mức handler đỏ** — ghi cho R3 |
| 2026-08-27 | `2a927a9` | 13 test concurrency chạy **10 vòng** trên cây đã sửa | **130 / 130 xanh** | 10 lần chạy riêng, thời gian từng lần: 0,650 / 0,480 / 0,462 / 0,477 / 0,465 / 0,456 / 0,479 / 0,504 / 0,502 / 0,473 s. Cộng dồn **4.400** lượt dispatch tranh một khoá và **160** lượt activation tranh một plant |
| 2026-08-27 | `2a927a9` | `make ci` sau R2 | **292 / 292 xanh** | 279 test ở C18 + 13 test concurrency của R2 |
| 2026-08-27 | `7934ac1` | ★ **Test đỏ khi bỏ diff so với revision đang hiệu lực** | **3 / 308** | Lab phá hoại của R3: `before` thành tập rỗng, tức hoàn nguyên thế giới một-tài-liệu. Cả ba là test diff, cùng triệu chứng *toàn bộ cây báo là added*. **12 test activation cũ vẫn xanh**. Cây làm việc R3, chưa commit. `ADR-024` |
| 2026-08-27 | `7934ac1` | Diff khi NV1 chuyển revision 1 → 2 | **+4 / −0** | 4 kênh sạc `FORM-01-CH-0005…0008`. NodeCount của NV1: 31 → 35 |
| 2026-08-27 | `7934ac1` | Diff khi NV1 chuyển revision 2 → 3 | **+1 / −1** | added `ASSEMBLY/L2/STACK-04`, removed `FORMATION/F1/FORM-02`. Lần đầu tiên `EquipmentPathsRemoved` khác rỗng |
| 2026-08-27 | `7934ac1` | Diff khi DE1 nhảy revision 1 → 3, bỏ qua 2 | **+1 / −0** | `PACK/P1/EOL-01`. Chứng minh diff tính so với thứ **đang có hiệu lực**, không phải với tài liệu đứng cạnh trên kệ |
| 2026-08-27 | `7934ac1` | `make ci` sau R3 | **309 / 309 xanh** | 292 sau R2 + **17** test của R3: 8 cho chuyển revision / thua CAS / restart, 9 cho catalog |
| 2026-08-27 | `0015a05` | ★ **Khai thác immutability chạy được trên code cũ** | **5 / 5** | R4. Sửa list sau `Create` · cast `Children` · cast `Segments` · cast `Sites` · sửa cây sau khi dựng index. Audit nêu 2, đo ra 5. Cây làm việc R4, chưa commit. `ADR-025` |
| 2026-08-27 | `0015a05` | Cây và flat index lệch nhau sau khi cây bị sửa | **42 so với 41** | Cùng lần chạy khai thác. `Find` trả `null` cho node đang nằm trong cây — không có exception nào |
| 2026-08-27 | `0015a05` | Đối chứng dương: nới **một** kiểu về `IReadOnlyList` | **1 / 10 đỏ** | Nới `IFactoryModelCatalog.Revisions`. Đúng một test đỏ, nêu đích danh member; test mutation lúc chạy vẫn xanh |
| 2026-08-27 | `0015a05` | `make ci` sau R4 | **319 / 319 xanh** | 309 sau R3 + **10** test immutability (5 fact + 5 ca reflection) |
| 2026-08-27 | `0c27971` | ★ **Test đỏ khi đọc CloudEvents bằng sentinel `ce_specversion`** | **1 / 14** | Lab phá hoại của `CloudEventContextExtensions`: khôi phục lối tắt *"thiếu `ce_specversion` thì trả `null`"*. Đúng ca `omitted: "ce_specversion"` đỏ; 5 ca thiếu-header còn lại vẫn xanh — một sentinel chỉ giấu được **chính nó**. `ADR-008` |
| 2026-08-27 | `0c27971` | ★ **Test đỏ khi bỏ `ConfigureSend` khỏi registration** | **1 / 14** | Chỉ `AnEventSentStraightToAnEndpoint_IsStampedToo` đỏ. Trước khi có test này, xoá cả một pipe khỏi đăng ký **không làm đỏ gì cả** — mọi test đều đi bằng `Publish` |
| 2026-08-27 | `0c27971` | ★ **Tên header `ce_*` duy nhất còn lại trong `_error` sau 5 lần thử** | **6 / 6** | `make bus-dlq` trên broker thật, exit 0. `rabbitmqadmin` in mỗi header **hai** dòng — một dòng `"ce_x": ` rỗng, một dòng `"ce_x":"giá trị` — nên đếm dòng cho **12** và làm DoD trượt oan trong khi broker giữ đủ sáu. Lab đếm **tên duy nhất** |
| 2026-08-27 | `0c27971` | Khoảng cách retry, lần chạy thứ hai | **249 / 383 / 903 / 1993 ms** | Cùng `NvmRetryPolicy` với lần đo `4e7fc02` (245 / 480 / 920 / 1933 ms). Hai lần chạy ra hai bộ số khác nhau — đó là **jitter đang hoạt động**, không phải sai số đo |
| 2026-08-27 | `0c27971` | `make ci` sau khi ép đủ bộ 6 header ở phía đọc | **328 / 328 xanh** | 320 + 8: 5 ca theory thiếu-header, 2 ca malformed (sai kiểu, rỗng), 1 direct-send. Phân bố: 282 unit · 23 analyzer · 17 architecture · 6 contract |

> [!warning] `bus` và `rabbitmq` không thay thế được cho nhau — và đây là lý do
> Hai probe bắt hai loại hỏng **khác nhau**, và mỗi cái mù với loại kia:
>
> | | broker chết **trước** khi app khởi động | broker chết **sau** khi bus đã chạy |
> |---|---|---|
> | `bus` | ✅ bắt được (25 ms) | ❌ **Healthy suốt 152 s** |
> | `rabbitmq` | ✅ bắt được | ✅ bắt được |
>
> `bus` là health check của MassTransit: nó nói về **bus trong process này** — đã khởi động chưa, các
> receive endpoint sẵn sàng chưa. `Nvm.Host.All` chỉ publish nên **không có receive endpoint nào**, và
> một bus đã khởi động xong thì không còn gì để báo hỏng. Xoá probe `rabbitmq` vì "MassTransit đã có
> health check rồi" là để lại đúng kịch bản nguy hiểm nhất — broker chết giữa ca — không ai canh.
>
> Ngược lại cũng không xoá được `bus`: nó là thứ giữ `ready` đỏ trong lúc bus đang khởi động, trước
> khi có connection nào để `rabbitmq` kiểm.

> [!important] Con số 18 nói lên cái gì, và không nói lên cái gì
> Nó **không** phải chỉ số chất lượng của RabbitMQ hay của MassTransit. Cả hai làm đúng việc của
> mình: broker chết thì không nhận được gì, và client báo lỗi thay vì nuốt.
>
> Nó là giá của việc **ghi hai nơi mà không có transaction nào nối chúng lại** — bẫy dual-write ở
> `scope.md` §5.5. Con số này tồn tại để trả lời câu hỏi mà M6 sẽ bị hỏi: *outbox thêm một bảng, một
> worker và một transaction, đổi lại được gì?* Đổi lại 18 event, trong 30 giây, ở một hệ thống chưa
> có tải.
>
> Cột `Commit` ghi `4e7fc02` vì phép đo chạy trên cây làm việc của C13 trước khi commit. **Đã đối
> chiếu ở R5**: hash thật của C13 đúng là `4e7fc02` (`M1-factory-model-bus.md` §7, checklist), nên
> không phải sửa gì. Ghi lại kết luận thay vì để nguyên câu dặn dò, vì một việc "phải làm" đã làm
> xong mà vẫn nằm đó sẽ được làm lại lần thứ hai.
>
> Cùng quy ước cho các dòng R2–R4 phía dưới: cột `Commit` là HEAD **lúc đo**, tức commit ngay
> trước đợt sửa đó, không phải commit chứa chính đợt sửa.

---

## M2 — Simulator, Ingestion & Idempotency

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-28 | `3a5ef0e` | ★ **Test đỏ khi sửa một field number trong `sparkplug_b.proto`** | **5 / 290** | Lab phá hoại của C01: `Metric.alias` từ `2` thành `20`. Chạy riêng `Nvm.UnitTests` (290 test). `dotnet build` vẫn **0 error, 0 warning** — compiler không có gì để nói. 3 trong 5 test đỏ là test decode payload thật, nên phép kiểm không chỉ dựa vào digest. Cây làm việc C01, chưa commit. `ADR-026` |
| 2026-08-28 | `3a5ef0e` | Alias decode được sau khi sửa field number | **0** (mong đợi 1, 2, 3, 4, 5) | Cùng lần chạy. `metric.HasAlias` = `False` và payload vẫn **parse thành công** — protobuf đọc field number lạ thành *unknown field* rồi đi tiếp |
| 2026-08-28 | `3a5ef0e` | `make ci` sau C01 | **336 / 336 xanh** | 328 sau M1 + **8** test của C01 (5 pin, 3 decode payload thật). Phân bố: 290 unit · 23 analyzer · 17 architecture · 6 contract |
| 2026-08-28 | `c80a905` | ★ **Test đỏ khi bỏ qua alias lạ thay vì ném** | **2 / 318** | Lab phá hoại A của C02: `throw new UnknownMetricAliasException` đổi thành `continue`. Chạy riêng `Nvm.UnitTests` (318 test lúc đo). `DecodeData` trả về **mảng rỗng** — không phân biệt được với report-by-exception báo *"không có gì đổi"* |
| 2026-08-28 | `c80a905` | ★ **Test đỏ khi bảng alias chỉ nhớ tên, không nhớ datatype** | **1 / 318** | Lab phá hoại B của C02: bỏ `declared ??= definition.DataType`. Triệu chứng: một `Int32` giá trị **−1** decode ra **4.294.967.295** — cùng chuỗi byte, không exception, và con số đó vẫn vào lọt cột `DOUBLE PRECISION` |
| 2026-08-28 | `c80a905` | ★ **Test đỏ khi type sinh từ `.proto` lọt ra public API** | **1 / 20** | Lab phá hoại C: thêm `public static Payload Raw(...)` vào `SparkplugPayload`. Rule **A7** đỏ và gọi tên đúng chỗ rò: `SparkplugPayload.Raw() returns`. Chạy riêng `Nvm.ArchitectureTests` |
| 2026-08-28 | `c80a905` | `make ci` sau C02 | **368 / 368 xanh** | 336 sau C01 + **32** test của C02 (29 unit, 3 architecture). Phân bố: 319 unit · 23 analyzer · 20 architecture · 6 contract |
| 2026-08-28 | `82f16b8` | `make ci` sau C03 | **401 / 401 xanh** | 368 sau C02 + **33** test của C03 (topic parse/format/round-trip, và resolve qua factory model thật). Phân bố: 352 unit · 23 analyzer · 20 architecture · 6 contract |
| 2026-08-28 | `2d847cc` | ★ **Test đỏ khi bỏ chuẩn hoá UTC khỏi `device_timestamp` trong natural key** | **1 / 379** | Lab phá hoại của C04 — lab **#2** của `scope.md` §9/M2 viết ngược lại thành regression. Bỏ `.ToUniversalTime()`: `07:15:30.5+00:00` và `14:15:30.5+07:00` là **cùng một thời điểm**, ra hai GUID. Chỉ **một** test bắt được, và con số 1 đó chính là điều đáng nhớ: tính chất này không có lớp phòng thủ thứ hai |
| 2026-08-28 | `2d847cc` | `make ci` sau C04 | **428 / 428 xanh** | 401 sau C03 + **27** test của C04. Phân bố: 379 unit · 23 analyzer · 20 architecture · 6 contract |

> [!note] Vì sao con số 5/290 quan trọng hơn nó trông có vẻ
> Nó không đo chất lượng của protobuf. Protobuf làm đúng việc của mình: field number lạ thì bỏ qua,
> vì đó là cách một schema tiến hoá được mà không phá bên đọc cũ.
>
> Nó đo **khoảng mù**: sửa đặc tả của tầng thiết bị là một thay đổi mà **build không thấy, test cũ
> không thấy, và runtime không ném gì**. Triệu chứng duy nhất là số metric ít đi. Trên một dây
> chuyền thật, triệu chứng đó là *"kênh sạc này không có dữ liệu"* — và người ta sẽ đi kiểm cáp
> trước khi nghĩ tới một file `.proto`.

---

## Mục tiêu SLO — còn phải đo

Khung lấy từ `docs/scope.md` Phụ lục A. Điền khi tới milestone tương ứng; mỗi ô điền xong phải
có một dòng chi tiết ở bảng phía trên.

| Chỉ số | Mục tiêu | M2 | M6 | M9 | M13 |
|---|---|---|---|---|---|
| Ingestion throughput (msg/s) | ≥ 5.000 | | | | |
| Ingestion lag p95 (s) | < 5 | | | | |
| Forward trace p95 (ms) | < 200 | — | | | |
| Backward trace p95 (ms) | < 150 | — | | | |
| Command API p95 (ms) | < 300 | — | | | |
| Projection lag p95 (s) | < 3 | — | | | |
| Rebuild toàn bộ (phút) | < 10 | — | | | |
| Matching 100k cell (s) | < 30 | — | — | | |
| CP-SAT vs greedy (% yield) | ≥ +8% | — | — | | |
| Cascade 3.000 pack (s) | < 60 | — | — | | |
| Test suite (phút) | < 10 | | | | |
| Mutation score domain (%) | ≥ 70 | — | — | — | |

`—` nghĩa là chỉ số đó chưa tồn tại ở milestone ấy, không phải "chưa đo".

---

## Chỉ số cố ý KHÔNG đo ở M1

| Chỉ số | Vì sao chưa |
|---|---|
| **N13 — toàn hệ thống healthy < 5 phút** | Đã đo ở **M0** (48 / 36 / 42 s so với ngưỡng 300 s). M1 **không thêm container nào** vào compose — RabbitMQ đã chạy từ M0/C08. Thứ M1 phải kiểm là *regression* của `/health/ready` khi số probe đi từ 5 lên 6, và nó đã có ở bảng trên. Bảng NFR `scope.md` §4 đã sửa N13 từ M1 sang M0 |
| Throughput bus (msg/s) | M1 publish 200 event cách nhau 100 ms — đó là kịch bản đo **mất mát**, không phải đo tải. Đo thật ở **M2** cùng N1 (≥ 5.000 msg/s) |
| Chi phí SHA-1 của `IdempotencyKey` | `ADR-010` §Evidence ghi rõ đây là **phán đoán chưa đo**. Đo ở M2 khi có tải thật |
| Ngưỡng kill switch | Chưa bật, vì chỉnh circuit breaker trên dữ liệu bằng 0 là đoán (`Nvm.Bus/README.md`). Bật và chỉnh ở M2 |
| Thời gian `make ci` | Vẫn như M0: số giây chỉ có nghĩa khi test đủ nhiều. 328 test chạy ~6 s — bắt đầu ghi từ M2 |
| Thời gian projection / truy vấn | Chưa có read model. Bắt đầu ở M6 |

---

## Chỉ số cố ý KHÔNG đo ở M0

Ghi lại để lần sau khỏi tưởng là quên:

| Chỉ số | Vì sao chưa |
|---|---|
| Thời gian `make ci` | C12 chỉ kiểm **đúng/sai** (exit 0 khi sạch, exit 2 khi phá format). Số giây chỉ có nghĩa khi đã có kha khá test — đo từ M2 |
| Throughput bất kỳ | Chưa có đường dữ liệu nào. Bắt đầu ở M2 |
| Độ trễ truy vấn | Chưa có read model. Bắt đầu ở M6 |
| Thời gian build | Solution mới có 3 project; số bây giờ không nói lên điều gì về sau |
