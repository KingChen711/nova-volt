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
6. **Dự đoán được phép, và chỉ được phép theo một cách**: ghi bằng chữ `DỰ ĐOÁN` ở cột `Chỉ số`,
   trong một commit **đứng trước** commit chứa số đo. Kiểm được bằng `git log`, không bằng trí nhớ.
   Một dự đoán viết sau khi đã biết kết quả tệ hơn không có dự đoán nào — nó biến một phép kiểm hiểu
   biết thành một câu chép lại. Luật này ra đời ở M3/C15-1 (`ADR-034`, `AGENTS.md` §5.8.4).

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
| 2026-08-30 | `d467095` | ★ **D1 — từ lúc file chạm inbox tới khi CẢ HAI consumer nhận xong** | **7.558 ms** | `make bus-fanout`. Ranh giới phải chép kèm con số: mốc đầu là lúc CSV chạm inbox, **không** phải lúc publish — ingestion không log lần publish thành công, và thêm một dòng log vào đường nóng chỉ để lab đọc được là sửa production cho tiện đo lường. Trong 7.558 ms đó có **5.000 ms** `PollInterval` + **2.000 ms** `SettleTime` của file watcher (`FileDropOptions`); phần còn lại — parse → dedup → transaction → publish CloudEvent → broker định tuyến → hai consumer xử lý — là **≈ 558 ms**. **Chế độ đo: chưa có hợp đồng publish.** Từ `ADR-035` (2026-09-02) file chỉ được đọc khi mang tên `*.csv.ready`, nên `SettleTime` không còn nằm trong hiệu số và 7.558 ms **chưa được đo lại** ở chế độ mới — không so sánh trực tiếp hai chế độ |
| 2026-08-30 | `d467095` | ★ **Giá của việc fan-out thêm một consumer** | **1 ms** | Cùng lần chạy: consumer thứ hai nhận sau consumer thứ nhất đúng **1 mili-giây** (04:16:09.801 và .802). Đây mới là con số nói về D1 — một publish, hai queue độc lập, và queue thứ hai không phải trả giá bằng việc chờ queue thứ nhất. Lần đo trước ở `4e7fc02` cho **4 ms** trên cùng lab |
| 2026-08-30 | `d467095` | Lab fanout từng in ra một hiệu số vô nghĩa và không ai biết | **−25.192.730 ms** | Lần chạy đầu sau khi thêm bấm giờ: `date` lấy giờ máy (UTC+7) còn BusLab ghi log bằng **UTC**, nên hiệu số ra âm bảy tiếng. Đã sửa mốc sang `date -u` **và** thêm chốt fail-closed — hiệu số âm hoặc > 120 s thì lab **im lặng không báo số** thay vì in ra một con số trông như phép đo. Cùng một bài học với J7/J8: một oracle sai thì con số của nó không phải là số |

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
| 2026-08-28 | `4f18f9a` | ★ **Test đỏ khi sửa một field number trong `sparkplug_b.proto`** | **5 / 290** | Lab phá hoại của C01: `Metric.alias` từ `2` thành `20`. Chạy riêng `Nvm.UnitTests` (290 test). `dotnet build` vẫn **0 error, 0 warning** — compiler không có gì để nói. 3 trong 5 test đỏ là test decode payload thật, nên phép kiểm không chỉ dựa vào digest. Cây làm việc C01, chưa commit. `ADR-026` |
| 2026-08-28 | `4f18f9a` | Alias decode được sau khi sửa field number | **0** (mong đợi 1, 2, 3, 4, 5) | Cùng lần chạy. `metric.HasAlias` = `False` và payload vẫn **parse thành công** — protobuf đọc field number lạ thành *unknown field* rồi đi tiếp |
| 2026-08-28 | `4f18f9a` | `make ci` sau C01 | **336 / 336 xanh** | 328 sau M1 + **8** test của C01 (5 pin, 3 decode payload thật). Phân bố: 290 unit · 23 analyzer · 17 architecture · 6 contract |
| 2026-08-28 | `4f18f9a` | ★ **Test đỏ khi bỏ qua alias lạ thay vì ném** | **2 / 318** | Lab phá hoại A của C02: `throw new UnknownMetricAliasException` đổi thành `continue`. Chạy riêng `Nvm.UnitTests` (318 test lúc đo). `DecodeData` trả về **mảng rỗng** — không phân biệt được với report-by-exception báo *"không có gì đổi"* |
| 2026-08-28 | `4f18f9a` | ★ **Test đỏ khi bảng alias chỉ nhớ tên, không nhớ datatype** | **1 / 318** | Lab phá hoại B của C02: bỏ `declared ??= definition.DataType`. Triệu chứng: một `Int32` giá trị **−1** decode ra **4.294.967.295** — cùng chuỗi byte, không exception, và con số đó vẫn vào lọt cột `DOUBLE PRECISION` |
| 2026-08-28 | `4f18f9a` | ★ **Test đỏ khi type sinh từ `.proto` lọt ra public API** | **1 / 20** | Lab phá hoại C: thêm `public static Payload Raw(...)` vào `SparkplugPayload`. Rule **A7** đỏ và gọi tên đúng chỗ rò: `SparkplugPayload.Raw() returns`. Chạy riêng `Nvm.ArchitectureTests` |
| 2026-08-28 | `4f18f9a` | `make ci` sau C02 | **368 / 368 xanh** | 336 sau C01 + **32** test của C02 (29 unit, 3 architecture). Phân bố: 319 unit · 23 analyzer · 20 architecture · 6 contract |
| 2026-08-28 | `e2bdad7` | `make ci` sau C03 | **401 / 401 xanh** | 368 sau C02 + **33** test của C03 (topic parse/format/round-trip, và resolve qua factory model thật). Phân bố: 352 unit · 23 analyzer · 20 architecture · 6 contract |
| 2026-08-28 | `9616bc9` | ★ **Test đỏ khi bỏ chuẩn hoá UTC khỏi `device_timestamp` trong natural key** | **1 / 379** | Lab phá hoại của C04 — lab **#2** của `scope.md` §9/M2 viết ngược lại thành regression. Bỏ `.ToUniversalTime()`: `07:15:30.5+00:00` và `14:15:30.5+07:00` là **cùng một thời điểm**, ra hai GUID. Chỉ **một** test bắt được, và con số 1 đó chính là điều đáng nhớ: tính chất này không có lớp phòng thủ thứ hai |
| 2026-08-28 | `9616bc9` | `make ci` sau C04 | **428 / 428 xanh** | 401 sau C03 + **27** test của C04. Phân bố: 379 unit · 23 analyzer · 20 architecture · 6 contract |
| 2026-08-28 | `5f188a2` | Dự đoán trước `make buffer-crash` | **20 / 200 vòng đỏ** | Chủ repo dự đoán trước khi chạy đủ 200 vòng. Smoke lỗi do MSYS đổi `/crash` thành đường dẫn Windows không tính vào kết quả buffer; harness được sửa rồi chạy lại từ đầu |
| 2026-08-28 | `5f188a2` | ★ `make buffer-crash` — vòng đỏ thực tế | **0 / 200** | Mỗi vòng writer và verifier là hai container khác nhau; writer đi qua cursor khác 0 rồi bị `SIGKILL`, không `Dispose`. Mọi digest đã `fsync` đều có mặt; mọi payload khớp SHA-256 + CRC; đọc hết không vượt cursor. Có vòng phục hồi thêm record chưa kịp ghi confirmation, đúng ngữ nghĩa ưu tiên duplicate hơn loss. `ADR-028` |
| 2026-08-28 | `5f188a2` | Gateway giữ buffer khi ingestion không tồn tại | **51.480 → 154.541 byte; 0 restart** | Recreate gateway mở lại `depth=257`; HTTP báo `Name or service not known (ingestion:8080)` nhưng retry đúng batch pending và segment tiếp tục tăng. Runtime check phát hiện rồi hồi quy hoá lỗi flusher từng đọc batch thứ hai khi cursor cũ chưa ACK |
| 2026-08-28 | `5f188a2` | Test sau C09 | **479 / 479 xanh** | 428 sau C04 + C05–C08 + **9** test C09: format/CRC/recovery/cap/rotation/cursor và retry batch pending. Phân bố: 430 unit · 23 analyzer · 20 architecture · 6 contract |

| 2026-08-29 | `191e3df` | `make ci` sau C10 | **500 / 500 xanh** | 479 sau C09 + **21** test C10 (rate limit, backoff, Retry-After, admission control). Phân bố: 442 unit · 23 analyzer · 20 architecture · 6 contract · 3 integration (Testcontainers) |
| 2026-08-29 | `84adcbb` | `make ci` sau C11 | **510 / 510 xanh** | + **10** test C11: NDEATH → STALE, `bdSeq` khớp phiên, `seq` nhảy cóc, alias bị bỏ khi phiên mới |
| 2026-08-29 | `113511f` | `make ci` sau C13 | **521 / 521 xanh** | + **11** test C13: ngưỡng 5 phút hai chiều, `Unknown`, ba timestamp khác nhau trong cùng row |
| 2026-08-29 | `79deb34` | `make ci` sau C15 | **547 / 547 xanh** | + C14 (9) + C15 (17). Golden file `quality/measurement-recorded.v1.json` |
| 2026-08-29 | `eb18620` | `make ci` sau C18 | **561 / 561 xanh** | + C16 (10) + C17 (1) + C18 (3). Phân bố: 493 unit · 23 analyzer · 20 architecture · 11 contract · 14 integration |
| 2026-08-29 | `b2b10e4` | ★ **D1 — đối chiếu 5 phút đồng hồ THẬT** | **282 = 282, lệch 0** | `make reconcile DURATION=300`, `TimeCompression=1`, 8 kênh, fault bật đủ. Bối cảnh cùng lần chạy: **69** duplicate bị dedup chặn · **52** row `Drifted` · **2** rebirth · **0** publish hỏng · p95 lag **30,8 s**. Bốn số bối cảnh đều khác 0 → phép kiểm có kiểm (R-M2-1). **Chưa chạy đủ 1 giờ** |
| 2026-08-29 | `b2b10e4` | Lab #1 — row thừa nếu bỏ dedup | **69 / 282 = 24,5 %** | Cùng lần chạy D1 ở trên. Đo bằng counter `nvm.ingest.duplicates`: mỗi delivery rơi vào `ON CONFLICT` là đúng một row sẽ thừa nếu không có khoá. Cao hơn 10 % của fault vì duplicate còn đến từ MQTT redelivery và từ rebirth khai lại |
| 2026-08-29 | `eb18620` | ★ **Lab #2 — row bị nuốt nếu bỏ `device_timestamp` khỏi khoá** | **65.628 / 65.676 = 99,93 %** | `NaturalKeyWithoutDeviceTimestampTests`, chạy trên output thật của `FormationLine`: một chu kỳ 18 giờ, 8 kênh, mẫu 5 giây. Còn lại đúng **48** khoá = 8 kênh × 6 signal. Cả đường cong formation dẹt thành một điểm, và **không có lỗi nào được ném** |
| 2026-08-29 | `eb18620` | ★ **D3 — message trên đĩa sau 2 phút tắt backend** | **6.272 message · 27.091.330 byte** | `make outage-lab OUTAGE=120`. Tắt `ingestion` + `timescale` + `rabbitmq`. Simulator `TimeCompression=60`, 8 kênh |
| 2026-08-29 | `eb18620` | ★ **D3 — số row lệch sau khi bật lại** | **0** (23.070 = 23.070) | Cùng lần chạy. Vế trái là `logicalMeasurements` của run, vế phải là row thêm vào. **Không mất message nào** trên chặng thiết bị → ingestion |
| 2026-08-29 | `eb18620` | **D3 — thời gian tiêu backlog** | **185 s** (ngân sách **180 s**) | Cùng lần chạy — **TRƯỢT 5 giây**. Bao gồm cả thời gian `timescale` + `rabbitmq` + `ingestion` khởi động lại. Tín hiệu `depth` đọc từ log mỗi `LogEvery=1000` message và poll 5 giây, nên độ phân giải của phép đo là ±5 s — nhưng 185 s là con số đo được và nó **không** được làm tròn xuống |
| 2026-08-29 | `eb18620` | ★ **D3 — số lần restart của simulator và gateway** | **0 và 0** | Cùng lần chạy. Đây là N15: backend chết không được làm dừng dây chuyền |
| 2026-08-28 | `cf61392` | Gateway restart khi backend tắt 2 phút — **trước khi sửa** | **6** | Lần chạy D3 đầu tiên. `HttpClient` timeout ném `TaskCanceledException`, kế thừa `OperationCanceledException`, nên bộ lọc `catch ... when (exception is not OperationCanceledException)` cho nó lọt và host dừng. Mỗi lần restart đọc lại buffer; backlog tiêu **183 s**. Sửa: lọc theo `stoppingToken` thay vì theo kiểu exception |
| 2026-08-29 | `eb18620` | ★ **Thời gian `make ci` — số đầu tiên của repo** | **1.106 s** (18 phút 26 giây) | `restore` → `format --verify` → `build -c Release` → `test` → `buffer-crash`. **563 test xanh**. M0 và M1 cố ý chưa đo con số này vì "số giây chỉ có nghĩa khi test đủ nhiều". Phần lớn thời gian là `buffer-crash`: 200 vòng, mỗi vòng hai container |
| 2026-08-29 | `eb18620` | ★ `make buffer-crash` chạy lại lúc đóng M2 | **0 / 200 vòng đỏ** | Trong `make ci`. Khớp con số đo ở C09 (`5f188a2`) — 200 vòng `SIGKILL` + reopen bằng process mới, không vòng nào mất record đã `fsync` |
| 2026-08-29 | `eb18620` | ★ **D2 — harness bắn 5.000 msg/s trong 10 phút** | **3.000.007 message · 5.000 msg/s · 0 lỗi** | `make load RATE=5000 DURATION=600`. Harness chạy TRONG `ot-net`, 8 publisher, 1 reading mỗi publish. **Fault lệch đồng hồ TẮT** — lag là hiệu hai đồng hồ, nên một đồng hồ sai sẽ được đo thành độ trễ đường ống |
| 2026-08-29 | `eb18620` | ★ **D2 — throughput đường ống ĐẦU-CUỐI thật sự đạt** | **936 msg/s** | Cùng lần chạy. Gateway decode **599.000**, forward **599.036**, `buffer_depth = 0` — gateway theo kịp thứ nó NHẬN ĐƯỢC, nhưng nó chỉ nhận được 1/5 |
| 2026-08-29 | `eb18620` | ★ **D2 — message rơi ở EMQX** | **≈ 2.400.971 / 3.000.007 = 80 %** | Cùng lần chạy. Yêu cầu của D2 là **0**. Harness publish 3.000.007, gateway chỉ thấy 599.036 — EMQX xả phần queue mà subscriber không kịp đọc. **D2 KHÔNG ĐẠT** |
| 2026-08-29 | `eb18620` | D2 — p95 lag device → DB | **2,42 s** (ngưỡng < 5 s) | Cùng lần chạy, 501.103 mẫu, chỉ tính row `clock_quality = Good`. p50 **0,24 s** · p99 **2,47 s** · max **2,48 s**. Con số này **chỉ nói về phần đã tới nơi** — nó không bào chữa cho 80 % rơi ở trên |
| 2026-08-29 | R2 pre-commit | R2 — Sparkplug protocol smoke 60 giây | **29.999 message · 500 msg/s · 0 publish lỗi · 0 sequence gap/rebirth** | Một MQTT connection cho `EDGE-F1`; `seq` tiếp tục từ NBIRTH qua 8 DBIRTH tới DDATA và wrap modulo 256. Simulator được dừng, gateway session tracker được restart trước run. 500 msg/s cố ý nằm dưới receiver capacity để cô lập contract session; **không thay D2 5.000 msg/s** |
| 2026-08-29 | working tree R2 | Chẩn đoán R3 ở 5.000 msg/s trong 60 giây sau khi sửa session | **300.003 source · 0 publish lỗi · 2.027 rebirth · 230.662 message bị EMQX drop** | Source giữ đúng 5.000 msg/s nhưng subscriber mqueue `max_len=1000` đầy; gateway chạy quanh **957 msg/s**. Đây là gap thật do receiver không theo kịp, không còn là tám counter `seq` xung đột. Chưa dùng để kết luận tối ưu trước R3 gate |
| 2026-08-29 | `eb18620` | D2 — throttle và rate limit của gateway | **0 và 0** | Cùng lần chạy. Ingestion chưa từng trả `429`, rate limit 12.000 msg/s chưa từng chạm. Nút thắt **không** ở ingestion mà ở chặng MQTT → gateway: MQTTnet dispatch tuần tự, mỗi message một vòng fsync |
| 2026-08-29 | `eb18620` | `make bus-fanout` trên đường thật | **2 / 2 consumer, cùng `ce_id`** | Publisher là Nvm.Ingestion, kích bằng file CSV thả vào inbox C15. Tắt `measurement-audit` → queue của nó **giữ 1 message chưa đọc**, `measurement-cache` về **0** — hai queue độc lập |
| 2026-08-29 | `eb18620` | `make bus-dlq` trên đường thật | **5 lần thử · 6 / 6 header `ce_*`** | Cùng bộ. `MT-Fault-RetryCount = 4` (đếm lần thử lại). `ce_source` = `urn:novavolt:nv1:ingestion` — envelope M1 sống sót qua publisher mới |
| 2026-08-29 | working tree R4 | ★ **D3 — thời gian tiêu backlog, đo bằng snapshot thật** | **41 s** (ngân sách **180 s**) | `make outage-lab`. Tắt `ingestion` + `timescale` + `rabbitmq` **133 s**; depth thường trực **113** → **7.502** khi bật lại. Dòng `eb18620` ghi **185 s** và kết luận *"trượt 5 giây"*; con số đó đọc `buffer_depth` từ log lấy mẫu mỗi 1.000 message và poll 5 giây. Cùng hệ thống, dụng cụ đo đúng: **41 s** |
| 2026-08-29 | working tree R4 | ★ **`buffer_depth = 0` KHÔNG bao giờ là trạng thái ổn định khi dây chuyền còn chạy** | **1 / 20 mẫu** | Lấy mẫu depth mỗi giây trong lúc simulator vẫn phát: chu kỳ `3 → 128 → 3` lặp lại vì flusher đọc từng batch 128 record. Lần chạy đầu của lab đợi `depth = 0` và **hết 180 giây ngân sách** trong khi backlog đã tiêu xong từ giây thứ 41. Ngưỡng đúng là *về lại mức thường trực trước khi tắt* |
| 2026-08-29 | working tree R4 | ★ **Chi phí đĩa thực của một record trong buffer** | **187,5 B/record** | Đo bằng hiệu số **trong** outage — lúc không có gì được acknowledge nên segment chỉ lớn thêm: `buffer_bytes` 7.160.862 → 8.283.473 (**+1.122.611 B**) trong khi `buffer_depth` 1.668 → 7.656 (**+5.988 record**). Một ước lượng cũ giả định **4,32 KiB/message** vì chia **tổng** byte buffer cho số record của riêng outage — lệch **≈ 23 lần** |
| 2026-08-29 | working tree R4 | D3 — số row lệch, lab fail-closed | **−6** (19.168 vs 19.174) | Cùng lần chạy 41 s ở trên. Simulator publish **17.982**, gateway decode **17.931**; `dropouts=1`, `heldHighWater=1473`. Dropout giữ 1.473 message trong RAM rồi xả một lượt, vượt `mqueue_max=1000` của subscriber. Lần chạy trước (dropout giữ 1.627) lệch **−184**. Cùng một nút thắt với D2 |
| 2026-08-29 | working tree R3 | ★ **D2 — cổng fail-closed bắt được điều `make load` cũ bỏ qua** | **exit 1, 7 mệnh đề trượt** | `make load RATE=5000 DURATION=600` qua `scripts/load-gate.sh`. Source **2.876.731** message / **4.794,55** msg/s / **0** publish lỗi; gateway decode = fsync = forward = **542.427**, reject **0**; row delta PostgreSQL **529.594**; peak p95 **7,87 s**; buffer depth cuối **0**. Simulator bị dừng để cô lập EDGE-F1. Target cũ in `/stats` rồi exit **0** trên đúng tình huống này |
| 2026-08-29 | working tree R3 | ★ **D2 — message EMQX xả vì subscriber không theo kịp** | **292.906 `send_msg.dropped.queue_full`** | Đọc từ EMQX dashboard API ~60 giây sau khi run bắt đầu. Client `nvm-edge-gateway`: `inflight_max=32`, `inflight_cnt=32` (bão hoà liên tục), `mqueue_len=1000/1000`. Client `nvm-load-EDGE-F1`: `recv_msg.dropped=0`. Đây là **số đo**, không phải suy diễn — nó thay cho câu *"mỗi message một vòng fsync"* ở dòng `eb18620` |
| 2026-08-29 | working tree R3 | ★ **Độ phân giải `device_timestamp` của Sparkplug B** | **1 mili-giây (`uint64` ms)** | Khoảng cách giữa hai `device_timestamp` liên tiếp trên `FORM-01-CH-0001`, 66.165 cặp: **26.699** cặp cách đúng **1.000 µs**, **15.929** cặp **2.000 µs**, **3.122** cặp **3.000 µs**, còn lại **≥ 4.000 µs**. Không cặp nào không phải bội số của 1.000 µs. `proto/sparkplug_b.proto` dòng 214: `optional uint64 timestamp` |
| 2026-08-29 | working tree R3 | ★ **Reading bị dedup nuốt vì trùng mili-giây — KHÔNG phải mất dữ liệu** | **12.833 / 542.427 = 2,37 %** | Cùng lần chạy 600 giây. `duplicates` của ingestion đi từ **41.976** lên **54.809** = đúng **12.833** = đúng hiệu giữa gateway forward và row delta. Khoá tự nhiên có `device_timestamp`, nên hai reading cùng signal trên cùng thiết bị trong cùng mili-giây **là một phép đo**. 8 kênh ở 4.794 msg/s = 599 msg/s mỗi kênh = cách nhau 1,67 ms |
| 2026-08-29 | working tree R3 | Kiểm chứng phép đếm message vs phép đo — 60 giây | **299.983 message · 277.465 phép đo riêng biệt · 22.526 trùng ms** | `NVM_LOAD_RATE=5000 NVM_LOAD_DURATION=60`. Gateway decode = fsync = forward = **61.724**; row delta **58.040** → **3.684** trùng ms trong phần nhận được. Harness đạt **4.999,72** msg/s, thiếu **17** message so với 300.000 — cổng vẫn trượt vì `>= 5.000` là ngưỡng cứng |
| 2026-08-29 | `eb18620` | ★ `make bus-chaos` trên đường thật | **1874 publish · 1874 nhận · 0 mất · 0 giao lại** | Broker tắt **30 s** giữa dòng traffic của simulator. Khác hẳn **18/200** của M1, và khác vì **publisher khác**: M1 publish đồng bộ qua dev endpoint trong lúc broker chết. **Không** kết luận dual-write đã đóng — lab này giết BROKER, không giết process ingestion. `ADR-022` vẫn mở tới M6 |
| 2026-08-29 | working tree R5 | ★ **D2 trước R5 — nút thắt thật nằm ở ingestion, không ở gateway** | **p95 99,11 s** · flush **4.141 msg/s** | `make load`. Gateway nhận **5.105 msg/s** và fsync kịp, nhưng flusher chỉ đẩy được **4.141 msg/s** nên `buffer_depth` tăng **960 record/giây** suốt 600 giây. `nvm-timescale` ở **93% CPU** — một backend PostgreSQL bão hoà đúng một core, vì flusher tuần tự nên cả đường ống dùng một connection |
| 2026-08-29 | working tree R5 | **Tuning bộ nhớ PostgreSQL — không phải câu trả lời** | 4.141 → **4.300 msg/s** | `shared_buffers` 128 MB → 768 MB, `max_wal_size` 1 GB → 8 GB, `mem_limit` 1 → 2 GiB. Giữ `synchronous_commit=on` và `full_page_writes=on`. Thay đổi không đáng kể: giới hạn là **CPU của một backend**, không phải cache miss |
| 2026-08-29 | working tree R5 | ★ **`WriterParallelism = 4` — thay đổi duy nhất thực sự gỡ nút thắt** | p95 **99,11 s → 2,81 s** · `buffer_depth` **604** | Một batch được chia cho 4 connection, mỗi connection một transaction. PostgreSQL phục vụ mỗi connection bằng một process, nên đây là cách duy nhất một batch chạm được nhiều hơn một core. Nhà máy thật đạt song song này miễn phí nhờ nhiều edge node; D2 chỉ đo một |
| 2026-08-29 | working tree R5 | ★ **Fixture phình làm số đo D2 trôi — cùng một commit** | **4.981 → 4.758 msg/s**, p95 **2,60 → 9,46 s** | Giữa hai lần chạy chỉ có bảng to ra: **18,3 triệu row**, **10,4 GB** table+index so với `shared_buffers` 768 MB. `source_event_id` là hàm băm của natural key nên không sắp được theo thời gian — mỗi row là một lần chèn ngẫu nhiên vào B-tree. Từ đây `load-gate.sh` và `reconcile.sh` TRUNCATE fixture trước khi chốt mốc đầu |
| 2026-08-29 | working tree R5 | ★ **D2 trên fixture sạch — lần 1** | **4.999,665 msg/s** · p95 **2,813 s** · lệch **0** | `make load RATE=5000 DURATION=600`. Source = gateway decode = fsync = forward = row delta = **2.999.807**, dedup **0**, `buffer_depth` cuối **0**, corrupt/truncated **0/0**, rebirth **0**. **Thiếu 201 message trên 3 triệu** (0,007%) so với ngưỡng cứng → **D2 vẫn TRƯỢT** |
| 2026-08-29 | working tree R5 | ★ **D2 trên fixture sạch — lần 2** | **4.993,968 msg/s** · p95 **4,390 s** · lệch **0** | Cùng lệnh, fixture được reset lại. Source = decode = fsync = forward = row delta = **2.996.389**, dedup **0**. Hai lần đo liên tiếp đều nằm dưới 5.000 → đây là trần thật của máy đo, không phải một lần xui |
| 2026-08-29 | working tree R5 | ★ **Trần của máy — chi phí fan-out của EMQX mới là giới hạn cuối** | **9.925 → 5.951 → 5.228 msg/s** | Harness không thết tốc độ, 45–60 giây, fixture sạch. **Không subscriber**: 9.925. **Một `mosquitto_sub` QoS 1** (không làm gì): 5.951 — mất 40% chỉ vì có người đọc. **Gateway thật**: 5.228. Gateway chỉ chiếm 12% phần hao; phần lớn là của broker |
| 2026-08-29 | working tree R5 | **Bốn giả thuyết tối ưu bị đo rồi BÁC BỎ** | đều **không** đổi trần | `FsyncBatchSize` 128→1024: **4.931** so với 5.104 (tệ hơn — batch rộng chỉ chờ lâu hơn). Cửa sổ in-flight 128→256: **5.104** so với 5.119 (broker ACK publisher mà không chờ subscriber). Server GC + `mem_limit` 1 GiB: **5.009**, và gateway chỉ dùng **54 MiB**. Cache parse topic: **5.197** so với 5.228. Cả bốn đã được hoàn nguyên — không giữ thay đổi không có số đỡ |
| 2026-08-29 | working tree R5 | ★ **Cửa sổ in-flight của harness — từ 32 lên 128 là thật** | **4.933 → 5.119 msg/s** | Không thết tốc độ. Ở 32, chính **dụng cụ đo** là thiết bị chậm nhất và D2 trượt vì harness chứ không vì đường ống. Giữ **256** để có biên cho stall, không phải để tăng throughput |
| 2026-08-29 | working tree R5 | ★ **764 row "mất" của D2 là dedup đúng, không phải mất dữ liệu** | **4.149 row `Drifted` chiếm chỗ trước** | Simulator nén thời gian nên phát `device_timestamp` đi trước đồng hồ thật — đo được **307.104** row có device_timestamp tới **2026-09-10**. Chúng ngồi sẵn trên natural key mà harness dùng lại khi thời gian thật đi tới đó. Toàn bộ 4.149 row va chạm đều là `Drifted`, toàn bộ row của run đều `Good`. Gate nay đòi `stored + deduped == source` |
| 2026-08-29 | working tree R5 | Thời gian `make ci` sau R5 | **964 s** (16 phút 4 giây) · **574/574** test · buffer-crash **0/200** | `restore` → `format --verify` → `build -c Release` → `test` → `buffer-crash`. Nhanh hơn 1.106 s của `eb18620` dù thêm 11 test; không đổi gì trong CI, nên chên lệch này là nhiễu máy chứ không phải cải thiện |
| 2026-08-29 | working tree R8 | ★ **Lab #3 — kích thước buffer sau 30 phút ở N1** | **9.071.178 record · 1.665.627.252 byte** (1,55 GiB) | `make backpressure-lab`. Nguồn là harness: **8.988.592** phép đo @ **4.993,66** msg/s, ingestion TẮT suốt 1.800 giây. **183,6 B/record** — khớp 187,5 B của R4 và 182 B của R5. §F4 từng nói lab này không chạy được vì sẽ cần 36,2 GiB; nó vừa **1,55 GiB**, nằm trong cap 2 GiB |
| 2026-08-29 | working tree R8 | ★ **Lab #3 lần A — xả KHÔNG rate limit** | **686 s · p95 1.987,256 s · 429/503: 0** | `NVM_EDGE_FLUSH_RATE=0`. 9.071.178 record → **13.224 msg/s** khi xả. Ingestion **chưa từng** trả 429 hay 503. Row delta **9.071.178**, dedup **0** — không mất gì |
| 2026-08-29 | working tree R8 | ★ **Lab #3 lần B — xả CÓ rate limit** | **851 s · p95 2.702,654 s · throttle 5.070** | `NVM_EDGE_FLUSH_RATE=12000`, cùng backlog đã chụp lại. Chậm hơn lần A **165 giây (+24%)**. 5.070 lần bị chính rate limiter của mình giữ lại; ingestion vẫn **0** lần trả 429/503. Row delta **9.071.178**, dedup **0** |
| 2026-08-29 | working tree R8 | ★ **Lab #3 — số row lệch giữa hai lần xả** | **0** (9.071.178 = 9.071.178) | Điều kiện bắt buộc của plan §5.C10.3: rate limit được phép làm chậm, **không** được phép làm mất. Hai lượt xuất phát từ cùng một bản chụp buffer nên đây là so sánh cùng điều kiện |
| 2026-08-29 | working tree R8 | ★ **Trần ingestion khi xả cao gấp 2,6 lần lúc chạy live** | **13.224 so với ~5.000 msg/s** | Lần A. Lúc xả không có MQTT receive tranh CPU, gateway chỉ đọc đĩa rồi POST — và ingestion hấp thụ được 13.224 msg/s. Xác nhận chẩn đoán R5: nút thắt của D2 nằm ở chặng MQTT → gateway, **không** ở ingestion |
| 2026-08-29 | working tree R9 | ★ **D1 — đối chiếu 1 GIỜ đồng hồ THẬT** | **3.540 = 3.540, lệch 0** | `make reconcile` (mặc định `DURATION=3600`). `TimeCompression=1` — run report ghi `processElapsed` đúng **01:00:00**, nên một giờ nhà máy đúng bằng một giờ đồng hồ. 8 kênh, fault bật đủ, fixture reset về bảng rỗng trước mốc đầu. Bối cảnh cùng lần chạy: **409** duplicate bị dedup chặn · **922** row `Drifted` · **2** rebirth · **0** publish hỏng. Bốn số đều khác 0 → phép kiểm có kiểm (R-M2-1) |
| 2026-08-29 | working tree R9 | ★ **Fault trùng 10 % thực sự chạy trong lần đo D1** | **290 / 2.845 = 10,19 %** | Cùng lần chạy. Simulator soạn **2.845** message logic và publish **3.135** — chính xác **290** bản gửi lần hai. Ingestion chặn **409**, cao hơn 290 vì duplicate còn đến từ rebirth khai lại và từ MQTT redelivery — cùng hình dạng với 69 / 282 của lần 5 phút |
| 2026-08-29 | working tree R9 | ★ **Fault lệch đồng hồ bám THIẾT BỊ, không bám message** | **2 / 8 kênh · 922 / 922 row của hai kênh đó** | Cùng lần chạy. `FORM-01-CH-0001` **436/436** và `FORM-01-CH-0003` **486/486** row là `Drifted`; sáu kênh còn lại **0**. Không có kênh nào lẫn hai loại — đúng ngữ nghĩa "một PLC có đồng hồ sai", không phải "một số message bị gắn cờ" |
| 2026-08-29 | working tree R9 | **p95 lag của D1 — do fault dropout, không do đường ống chậm** | **25,559 s** (p50 **1,170** · p99 **36,962** · max **42,010**) | Cùng lần chạy, **2.618** mẫu `Good`. **2.321 / 2.618 = 88,66 %** nằm dưới 5 s; phần còn lại trải đều tới 42 s — đúng hình dạng **11** lần dropout × 30 s giữ message trong RAM rồi xả một lượt (`heldHighWater` **33**). **D1 không phát biểu ngưỡng lag**; ngưỡng < 5 s là của D2, và D2 chạy với fault dropout lẫn lệch đồng hồ **TẮT**. Hai con số không so sánh được với nhau |
| 2026-08-29 | working tree R9 | ★ **`reconcile.sh` từng in p95 của NGƯỜI KHÁC** | **1.611 s** báo cáo so với **41,8 s** thực tế | Lượt khói 120 giây ngay trước lần đo D1. `lagP95Seconds` của `/stats` là percentile trên ring **8.192** mẫu (`IngestionLag.WindowSize`) — đúng cho D2 vì ba triệu message lấp đầy ring, **không bao giờ đúng cho D1** vì một giờ chỉ sinh 2.618 mẫu. Con số 1.611 s là của lab backpressure R8 còn sót trong ring. Từ R9, `reconcile.sh` tính `percentile_disc` trên row commit sau mốc đầu. Dòng D1 `b2b10e4` ghi p95 **30,8 s** đọc từ ring cũ nên **không so sánh được** với 25,559 s ở trên |
| 2026-08-29 | working tree D | ★ **D2 với oracle đã sửa — lượt 1, offer 5.100** | **4.710,733 msg/s** · p95 đỉnh **9,144 s** · **TRƯỢT** | `RATE=5100 DURATION=600`. Rate tính bằng **elapsed thật** (600,058 s), không phải thời lượng yêu cầu. Mọi vế đúng đắn đạt tuyệt đối: source = gateway decode = fsync = forward = row delta = **2.826.722**, dedup **0**, reject **0**, corrupt/truncated **0/0**, `buffer_depth` cuối **0**, **EMQX dropped 0**. Receiver sustained **4.711,522 msg/s** trong 571 s giữa cửa sổ — ingestion theo kịp **đúng bằng** thứ nó nhận được. Ingestion CPU đỉnh **55,6 %**, RAM đỉnh **83,4 MiB** |
| 2026-08-29 | working tree D | ★ **D2 với oracle đã sửa — lượt 2, offer 5.000** | **4.465,391 msg/s** · p95 đỉnh **9,567 s** · **TRƯỢT** | Cùng lệnh, chỉ đổi offer. **Offer cao hơn KHÔNG làm hại**: lượt offer 5.100 đạt 4.711, lượt offer 5.000 đạt 4.465. Hai lượt cách nhau 15 phút lệch **5 %** — đó là độ phân tán của máy đo, không phải hiệu ứng của offer. Mọi vế đúng đắn lại đạt tuyệt đối: **2.679.501** khắp chuỗi, dedup 0, EMQX dropped 0. CPU **61,3 %**, RAM **91,7 MiB** |
| 2026-08-29 | working tree D | ★ **Con số 4.999,665 msg/s cũ được tính bằng thời lượng YÊU CẦU** | **4.465 – 4.711** so với **4.999** | `LoadRunner` chia số message gửi cho `_options.Duration` chứ không cho `elapsed` đo được — harness tự chấm điểm theo ý định của mình. Sửa xong và chạy lại hai lần, nguồn không lần nào chạm N1. Kết luận *"chỉ còn thiếu 0,007 %"* ở §F5 **không còn đứng vững**; khoảng cách thật là **6 – 11 %**. Trần máy đo 5.228 msg/s của R5 chỉ cao hơn N1 **4,5 %**, nên máy chậm đi một chút là D2 rơi xuống dưới |
| 2026-08-29 | working tree D | ★ **p95 của D2 từng đạt nhờ lấy mẫu thưa** | **9,1 – 9,6 s** trên 121–122 mẫu | Sampler đổi từ **10 s** xuống **5 s**, nên độ phủ của phép đo tăng gấp đôi (mỗi lần đọc `/stats` mô tả p95 của ~1,7 giây gần nhất ở tốc độ này). Hai lượt độc lập đều thấy một khoảng nghẽn ~9 s. Dòng **2,81 s** và **4,39 s** của R5 đo ở độ phủ một nửa và **không so sánh được** với hai số này. Đây là số đo, không phải suy diễn: cùng đường ống, dụng cụ nhạy hơn |
| 2026-08-29 | working tree D | **Nút thắt D2 KHÔNG nằm ở ingestion** | CPU **55,6 / 61,3 %** · RAM **83,4 / 91,7 MiB** | Hai lượt trên, `docker stats` mỗi 5 giây. Plan C16 đòi thu hai số này và trước đây chưa ai thu. Ingestion còn xa mới bão hoà, và receiver sustained bám sát source từng msg/s — thứ chặn D2 nằm ở chặng nguồn → EMQX → gateway, đúng chẩn đoán của R5 và của lab #3 (ingestion hấp thụ 13.224 msg/s khi không phải tranh CPU với MQTT receive) |
| 2026-08-29 | working tree E | ★ **D3 ĐẠT — oracle nghiêm, cả ba vế** | **0 mất · 28 s · 0 restart** | `make outage-lab`. Tắt `ingestion` + `timescale` + `rabbitmq` **152 giây** (≥120). Vế trái **16.429** = vế phải **16.429**, lệch **0**. Backlog tiêu hết trong **28 s** trên ngân sách **180 s**, `buffer_depth` cuối **0**, `forwarded == buffered`. Simulator và gateway **0 restart**. Khác mọi lần trước ở chỗ đồng hồ chỉ chạy trên một backlog **hữu hạn** |
| 2026-08-29 | working tree E | ★ **Message trên đĩa sau 120 giây tắt backend** | **7.313 record · 35.153.897 byte** | Cùng lần chạy, `TimeCompression=60`, 8 kênh. Mức đầy thường trực trước khi tắt là **114**. Khi bấm giờ, backlog đã đứng yên ở **7.353** record — lớn hơn 7.313 vì `FlushAsync` của simulator xả nốt phần đang giữ trong RAM sau khi nó dừng |
| 2026-08-29 | working tree E | ★ **N15 bằng số: dây chuyền vẫn ĐO trong lúc backend chết** | **7.864 phép đo sinh ra trong outage** | Cùng lần chạy. Trước đây lab chỉ kiểm container còn `Running` — một simulator sống nhưng đã ngừng phát cũng trả về `true`. Hiệu số `logicalMeasurements` giữa lúc tắt và lúc dừng là con số duy nhất nói được dây chuyền **vẫn làm việc** |
| 2026-08-29 | working tree E | Chi phí đĩa mỗi record — lần đo thứ ba | **194 B/record** | Cùng lần chạy, đo bằng hiệu số **trong** outage: 1.400.442 B / 7.199 record. Khớp **187,5 B** của R4 và **183,6 B** của R8. Ba lần đo độc lập trong khoảng 183–194 B; §F4 từng giả định **4,32 KiB** |
| 2026-08-29 | working tree E | Đoán trước khi đo — D3 (`AGENTS.md` §5.8.4) | **2/3 trúng** | Chủ repo đoán trước khi chạy: message trên đĩa **6.000–9.000** (thực tế **7.313** ✔), row lệch **đúng 0** (thực tế **0** ✔), thời gian xả **30–60 s** (thực tế **28 s** — nhanh hơn cận dưới). Chỗ đoán lệch là chỗ học được: ingestion sau R5 hấp thụ backlog nhanh hơn trực giác, và đồng hồ mới **bao gồm** cả thời gian ba container khởi động lại |
| 2026-08-30 | working tree H | ★ **D1 chạy lại sau thay đổi runtime — trùng TỪNG con số** | **3.540 = 3.540, lệch 0** | `make reconcile` sau khi Unit A đổi `SimulatorWorker`, `MqttSparkplugPublisher` và `FormationLine`. Không chỉ tổng khớp: **409** duplicate, **922** row `Drifted`, **2** rebirth, **0** publish hỏng, **2.618** mẫu `Good` — giống hệt lần đo ở `f60ba15`. Simulator là tất định (Random có seed, profile cố định, `TimeCompression=1`), nên trùng khít là bằng chứng đường **connected** không đổi một message nào |
| 2026-08-30 | working tree H | p95 lag của D1 — thứ DUY NHẤT đổi giữa hai lần | **21,305 s** so với **25,559 s** | Cùng lần chạy. p50 **1,106** · max **42,123** (lần trước 1,170 và 42,010). Lag đo bằng đồng hồ **thật**, còn dây chuyền chạy bằng thời gian quá trình — nên 11 lần dropout × 30 giây rơi vào những chỗ khác nhau của đồng hồ treo tường ở hai lần chạy. Số phép đo thì không được phép đổi, và nó không đổi |
| 2026-08-30 | working tree F5 | ★ **Harness chỉ thấy 100/1.000 kênh của topology load** | **1000** so với **100** | `ChannelsOf` quét cứng `FORM-01-CH-0001..2000`, đúng với seed demo một cycler nhưng chỉ thấy **một phần mười** một seed mười cycler. `deploy/seed-load/` đúng hình dạng đó, nên `NVM_SEED_DIR=seed-load make load` đáng lẽ chạy **100** kênh và báo số của một nhà máy khác. Sau khi đọc `model.Paths` như simulator vẫn làm: harness in ra **1000 channels** với `seed-load` và **8 channels** với seed demo |
| 2026-08-30 | working tree F5 | ★ **Harness tự chấm mình theo OFFERED rate — cùng lỗi, thấp hơn một tầng** | **exit 1** dù đường ống exact | `Program.cs` trả exit 1 khi `AchievedRate < options.Rate`. Với offer 5.100 và đạt 4.825,8 msg/s, một lượt **2.895.717 message exact khắp chuỗi, EMQX dropped 0, receiver bám source** vẫn đỏ — nên verdict `D2/M2 PASS` là thứ **không lượt nào chạm tới được**. Ngưỡng thuộc về `load-gate.sh` và về N1 (`ADR-031`), không thuộc về thứ process này được yêu cầu thử |
| 2026-08-30 | working tree F5 | ★ **D2/M2 PASS — verdict đầu tiên thực sự chạm tới được** | **2.841.506 exact · EMQX dropped 0** | `make load` chế độ M2 (`ADR-031`). Source = gateway decode = fsync = forward = row delta = **2.841.506**, dedup 0, reject 0, buffer cuối 0, corrupt/truncated 0/0, sequence gap 0. Receiver sustained **4.799,781** so với source **4.735,406** msg/s — theo kịp, không tích luỹ backlog. Script in đúng ba dòng CHƯA ĐẠT của N1/N2 và trỏ sang M13. Ingestion CPU đỉnh **89,0 %** / RAM **115,2 MiB** — cao hơn hai lượt trước (52–61 %), tức CPU ingestion cũng dao động mạnh trên rig này |
| 2026-08-30 | working tree J2 | ★ **D3 TRƯỢT sau khi đồng hồ bắt đầu đúng chỗ** | **lệch −5 row** (16.582 vs 16.587) | `make outage-lab` mặc định. Hai vế kia **đạt**: tắt **143 s** ≥ 120, restart **0/0**, **7.972** phép đo sinh ra trong outage. `DRAIN_START` nay đặt **trước** `docker compose start`, gate assert **strict `< 180`**. Đây là lần D3 đầu tiên được chấm bằng đúng mệnh đề của nó, và nó không đạt |
| 2026-08-30 | working tree J2 | **D3 — thời gian tiêu backlog, đồng hồ BAO GỒM restart** | **46 s** (ngân sách strict < **180 s**) | Cùng lần chạy. Backlog hữu hạn **7.580** record, depth cuối **0**, `forwarded == buffered`. Dòng **28 s** ngày 2026-08-29 không so sánh được: nó bấm giờ **sau** khi ba container đã lên. Chính **18 giây** chênh lệch đó là thứ J2 nói tới |
| 2026-08-30 | working tree J2 | Đoán trước khi đo — D3 lần hai (`AGENTS.md` §5.8.4) | **3/3 trúng** | Chủ repo đoán trước khi chạy: backlog **6.000–9.000** (thực tế **7.580** ✔), row lệch **0** (thực tế **−5** ✘), drain **30–60 s** (thực tế **46 s** ✔). Câu đoán sai là câu đáng giá: cả hai người đều tin phép đối chiếu đã kín, và nó không kín |
| 2026-08-30 | working tree J2 | ★ **Nguyên nhân −5: `logicalMeasurements` đếm lúc SOẠN, không phải lúc PHÁT** | **decoded 15.179 = published 15.181 − 2 NBIRTH** | Không mất gì trên chặng thiết bị → ingestion: gateway decode = forward, reject **0**, depth cuối **0**, `telemetry` = `processed_message` = **16.582**. `FormationLine.Advance` tăng `MeasurementCount` cho **cả** batch rồi mới truyền batch đó cho `PublishLockedAsync`, nơi vòng lặp có thể thoát sớm vì cancel/session/publish lỗi. Nên vế trái đếm phép đo **đã soạn**, còn vế phải đếm phép đo **đã phát** — hai tập khác nhau mỗi khi lab dừng nguồn giữa một tick |
| 2026-08-30 | working tree J2 | Khoảng cách giữa hai device timestamp liền nhau — loại bỏ giả thuyết va chạm mili giây | **nhỏ nhất 5 giây** | Truy vấn `lag()` theo (equipment, signal) trên 16.582 row: **1** khoảng 5 s, **11.138** khoảng 10 s, phần còn lại ≥ 15 s. Không có hai phép đo nào chung một mili giây, nên **−5 không phải** do dedup nuốt va chạm natural key — khác với 764 "mất" của D2 ngày 2026-08-29 |
| 2026-08-30 | working tree J5 | ★ **D2/M2 PASS trên topology 1.000 kênh — lần đầu chạy đúng thứ `scope.md` cam kết** | **2.361.174 exact · EMQX dropped 0** | `make load` mặc định `NVM_SEED_DIR=seed-load`. Banner harness in **`across 1000 channels`**, **1.000** DBIRTH được khai. Source 2.360.174 message + 1.000 birth = gateway decode = fsync = forward = row delta = **2.361.174**, dedup **0**, reject 0, buffer cuối 0, corrupt/truncated 0/0, sequence gap 0, publish lỗi 0. Mọi lần đo D2 trước đây chạy **8** kênh |
| 2026-08-30 | working tree J5 | ★ **Giá của cardinality: 1.000 kênh so với 8 kênh, cùng rig, cùng commit** | **3.933,2** so với **4.735,4 msg/s** | Chậm hơn **17 %**. Cùng offer 5.100 msg/s, cùng 600 giây. Receiver sustained **3.950,4** — vẫn bám source (không tích luỹ backlog), nên nút thắt nằm ở chặng nguồn → EMQX → gateway chứ không ở ingestion: CPU đỉnh **66,0 %**, RAM đỉnh **92,3 MiB** |
| 2026-08-30 | working tree J5 | ★★ **p95 lag ở 1.000 kênh — cách N2 một bậc độ lớn** | **99,525 s** so với ngưỡng **< 5 s** | 121 mẫu, ring `lagSamples` đã đầy bằng dữ liệu của chính run này. Trên 8 kênh cùng dụng cụ đo được **9,14** và **9,57 s**. Gấp **10 lần** số của 8 kênh và **gấp 20 lần** ngưỡng N2. Đây là số đo, không phải suy diễn — và nó nói rằng khoảng cách tới N2 lớn hơn nhiều so với những gì bài đo 8 kênh gợi ra |
| 2026-08-30 | working tree J5 | Bao nhiêu phần của D2 là DBIRTH ở topology này | **1.000 / 2.361.174** | Mỗi kênh khai đúng một lần: 1.000 DBIRTH dưới **một** NBIRTH và **một** `seq` stream, không sequence gap và không rebirth trong suốt 600 giây. Đây là mệnh đề cardinality của `deploy/seed-load/` chạy trên đường thật thay vì trong `ThousandChannelTopologyTests` |
| 2026-08-30 | working tree J7 | ★★ **Vế trái D1/D3 đếm lúc PHÁT — lệch −5 biến mất** | **16.483 = 16.483, lệch 0** | `make outage-lab` sau khi `MeasurementCount` chuyển sang tăng trong `PublishLockedAsync`, ngay cạnh `LogicalMessageCount++`. Cùng lab, cùng oracle J2, cùng tham số như lần đo −5 hai giờ trước. Đây là lab phá hoại đọc ngược: bỏ cơ chế đi thì lệch **−5**, để cơ chế vào thì lệch **0** |
| 2026-08-30 | working tree J7 | ★★ **D3 vẫn TRƯỢT — nhưng ở vế khác, và vì một lý do khác hẳn** | **drain 181 s** (ngân sách strict < **180 s**) | Lần chạy thứ nhất sau khi sửa J7: tắt **142 s** ✔ · restart **0/0** ✔ · **7.912** phép đo trong outage ✔ · lệch **0** ✔ · backlog **7.420** record · depth cuối **0**. Chỉ thiếu **1 giây**, và một giây đó không nằm ở đường ống — xem hai dòng dưới |
| 2026-08-30 | working tree J7 | ★★ **166 / 181 giây phục hồi là gateway NGỦ, không phải gateway BẬN** | **7 lần thất bại · 0,8 giây xả thật** | Log `nvm-edge-gateway` cùng lần chạy: `22:41:19` *"flush failed 1 times; retrying after 00:00:02"* (Connection refused) → `22:44:05` *"accepted a flush again after 7 failures; buffer depth=7331"* → `22:44:05.120` tới `22:44:05.922` xả **7.331 record**. Tổng backoff 2+4+8+16+30+30+30 giây, trần `FlushRetryMaxDelay` **30 s** + jitter 25 %. Thời gian truyền dữ liệu thật: **0,8 giây** cho toàn bộ backlog, tức **≈ 9.100 record/s** |
| 2026-08-30 | working tree J7 | ★★ **D3 chạy lại trên cây cuối — lệch 0 lần thứ hai, drain đúng 180 s** | **16.426 = 16.426 · 180 s** | Lần chạy thứ hai sau khi sửa J7, trên đúng cây sẽ commit. Tắt **140 s** ✔ · restart **0/0** ✔ · **7.869** phép đo trong outage ✔ · lệch **0** ✔ · backlog **7.340** record · depth cuối **0** ✔ · drain **180 s** ✘ — chạm đúng ngân sách, và `< 180` là strict. Đây chính là trường hợp assert của J2 được viết ra để bắt, gặp thật ngay lần thứ hai |
| 2026-08-30 | working tree J7 | ★★ **Ba lần đo nói backoff quyết định D3, không phải đường ống** | **7 lần hỏng · 7 lần hỏng · drain 181 / 180 s** | Hai lần chạy liên tiếp đều ghi *"accepted a flush again after **7** failures"* và đều cho drain **181** rồi **180 giây** trên backlog **7.420** và **7.340** record. Lần **46 giây** trước đó là ngoại lệ — backend sẵn sàng đúng lúc một retry vừa bắn. Với outage 120 giây, lịch 2+4+8+16+30+30+30 luôn đưa gateway vào vùng chờ 30 giây, nên **~180 giây là giá trị thường trực chứ không phải xui**: D3 không có đường đạt ổn định trước khi J8 được xử lý |
| 2026-08-30 | working tree J8 | ★★ **ĐÍNH CHÍNH: 181/180 giây là đồng hồ của waiter, không phải của đường ống** | **drain 37 s** trên cùng lab, cùng tham số | `make outage-lab` sau khi `wait_for_forward_quiescence` đổi sang delta từ một mốc đã chốt. Oracle cũ đòi `buffered == forwarded`, hai counter đếm theo đời TIẾN TRÌNH, trong khi buffer và cursor sống qua restart — đo được ba snapshot liên tiếp `depth=0` mà `33616 != 33673`, lệch **57** vĩnh viễn. Vòng đợi vì thế chờ hết ngân sách trên một backlog đã sạch. **Hai dòng 181 và 180 giây ở trên vẫn là output thật của oracle cũ và không bị sửa** |
| 2026-08-30 | working tree J8 | ★★ **D3 ĐẠT — lần đầu tiên cả hai oracle đều đúng** | **16.288 = 16.288 · drain 37 s** | Tắt **141 s** ≥ 120 ✔ · restart **0/0** ✔ · **7.717** phép đo sinh ra trong outage ✔ · backlog hữu hạn **7.288** record · depth cuối **0** ✔ · lệch **0** ✔ · `abandonedMeasurements` **0** ✔ · reject **0** · publish 14.931 / decode 14.929 (chênh đúng **2** NBIRTH). Ngân sách strict **< 180 s**, đồng hồ vẫn bấm TRƯỚC `docker compose start`. Vế trái là phép đo **sinh ra** (J7 bản hai), vế phải là row |
| 2026-08-30 | working tree J8 | Đích forward chốt tại snapshot hữu hạn — khớp tuyệt đối | **58.235 + 7.288 = 65.523**, thực tế **65.523** | Cùng lần chạy. Cả hai số hạng đọc từ **cùng một** snapshot, chốt lúc nguồn đã dừng và backend vẫn tắt. Khoản lệch lịch sử nằm trong số hạng thứ nhất nên tự triệt tiêu; `-eq` chứ không `-ge`, nên forward vượt đích cũng là trượt |
| 2026-08-30 | working tree J8 | **Giá thật của trần backoff 30 giây: ~30 giây chờ, không phải 166** | **throttled 0 · rate_limited 0 · drain 37 s** | 37 giây ấy gồm cả ba container lên lại **và** một nhịp backoff. Con số **166 giây** ghi ngày trước đo từ lần **fail đầu tiên** — tức từ giữa outage 120 giây có chủ ý — chứ không phải từ lúc backend sẵn sàng. **Backoff không chặn D3**, và câu hỏi thiết kế *Connection refused* vs *503* vẫn mở nhưng không còn được biện minh bằng “D3 không có đường đạt” |
| 2026-08-30 | working tree J8 | Đoán trước khi đo — D3 lần thứ tư (`AGENTS.md` §5.8.4) | **3/3 trúng** | Chủ repo đoán trước khi chạy: drain **< 60 s** (thực tế **37** ✔), backlog **7.000–7.600** (thực tế **7.288** ✔), `abandonedMeasurements` **đúng 0** (thực tế **0** ✔). Lần đầu tiên cả ba câu đều trúng, và câu drain trúng vì chủ repo tin phép đo mới hơn tin kết luận cũ |
| 2026-08-30 | working tree J8 | **Lần chạy bị huỷ: gate mới của chính mình hỏng** | **drain 48 s**, verdict **TRUỢT** | Lần chạy ngay trước đó. Mọi phép đo đều đạt (lệch **0**, 16.267 = 16.267, backlog **7.327**, đích 50.594/50.594, abandoned **0**) nhưng script báo đỏ: dòng assert `abandonedMeasurements` chứa hai ký tự `\n` thay vì xuống dòng, nên `[` thiếu `]`, luôn lỗi, luôn gọi `fail`. **`sh -n` không bắt được** — cú pháp vẫn hợp lệ. Kiểm lại bằng cách chạy chính hai dòng assert với 4 bộ giá trị, cả hướng đúng lẫn hướng sai |
| 2026-08-30 | `f60ba15` | ★★ **D1 chạy lại trên oracle mới — trùng TỪNG con số với oracle cũ** | **3.540 = 3.540, lệch 0** | `make reconcile` mặc định (`DURATION=3600`, `TimeCompression=1`) trên HEAD có cả J7 bản hai lẫn J8. Vế trái nay đếm **lúc đo** ở `Declare`/`Sample` chứ không phải sau `PublishAsync`. Bối cảnh trùng khít lần đo `working tree H` và lần đo `f60ba15`: **409** duplicate bị chặn · **922** row `Drifted` · **2** rebirth · **0** publish hỏng · **2.618** mẫu `Good`. Simulator là tất định, nên trùng khít qua một lần **đổi oracle** là bằng chứng bản sửa không đụng vào một message nào |
| 2026-08-30 | `f60ba15` | ★★ **Trên workload của D1, hai cách đếm cho cùng một số** | `abandonedMeasurements` **0** · vế trái **3.540** ở cả hai oracle | Cùng lần chạy, và đây là điều làm bằng chứng D1 **cũ** không bị hạ: suốt một giờ với ~10 dropout được tiêm, không batch nào bị bỏ, nên *"đếm lúc đo"* và *"đếm sau publish"* không thể lệch nhau. Hai oracle chỉ tách ra khi có mất mát thật — đúng lúc oracle cũ mù, và đó là lý do nó vẫn phải đổi dù con số hôm nay không đổi |
| 2026-08-30 | `f60ba15` | p95 lag của D1 — số duy nhất dao động giữa ba lần chạy | **25,298 s** (trước: 25,559 và 21,305) | Cùng lần chạy. p50 **1,105** · max **40,838** · **2.618** mẫu `Good` (hai lần trước: p50 1,106 và 1,170; max 42,123 và 42,010). Lag đo bằng đồng hồ **thật** còn dây chuyền chạy bằng thời gian quá trình, nên các lần dropout rơi vào những chỗ khác nhau của đồng hồ treo tường. Số phép đo thì không được phép đổi, và qua ba lần nó không đổi |
| 2026-08-30 | `f60ba15` | Đoán trước khi đo — D1 (`AGENTS.md` §5.8.4) | **3/3 trúng** | Chủ repo đoán trước khi chạy: vế trái **đúng 3.540 như cũ** (thực tế **3.540** ✔), lệch **đúng 0** (thực tế **0** ✔), `abandonedMeasurements` **đúng 0** (thực tế **0** ✔). Câu thứ nhất là câu đáng giá: nó phát biểu rằng đổi chỗ đặt counter **không được** làm đổi con số trên một run không mất gì — và đó chính là chỗ phân biệt một bản sửa oracle với một bản sửa số |



> [!note] Vì sao con số 5/290 quan trọng hơn nó trông có vẻ
> Nó không đo chất lượng của protobuf. Protobuf làm đúng việc của mình: field number lạ thì bỏ qua,
> vì đó là cách một schema tiến hoá được mà không phá bên đọc cũ.
>
> Nó đo **khoảng mù**: sửa đặc tả của tầng thiết bị là một thay đổi mà **build không thấy, test cũ
> không thấy, và runtime không ném gì**. Triệu chứng duy nhất là số metric ít đi. Trên một dây
> chuyền thật, triệu chứng đó là *"kênh sạc này không có dữ liệu"* — và người ta sẽ đi kiểm cáp
> trước khi nghĩ tới một file `.proto`.

---

## M3 — Telemetry, TimescaleDB & Production Calendar

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-08-30 | `26c752b` | ★ **Độ dài thật ca C — `DE1`, 28/03/2026** | **7 giờ** | `ProductionCalendarDaylightSavingTests`. Ranh giới `2026-03-28T22:00+01:00` → `2026-03-29T06:00+02:00`. Đồng hồ nhảy 02:00 → 03:00 |
| 2026-08-30 | `26c752b` | ★ **Độ dài thật ca C — `DE1`, 24/10/2026** | **9 giờ** | Cùng bộ test. Ranh giới `2026-10-24T22:00+02:00` → `2026-10-25T06:00+01:00`. Đồng hồ lùi 03:00 → 02:00 |
| 2026-08-30 | `26c752b` | Độ dài thật ca C — `NV1`, cùng hai ngày | **8 giờ** và **8 giờ** | Cùng bộ test. `Asia/Ho_Chi_Minh` là UTC+7 cố định — đây là vế đối chứng, không phải vế chứng minh |
| 2026-08-30 | `26c752b` | Độ dài cả production day ở `DE1` | **23 giờ** (28/03) · **25 giờ** (24/10) | Cùng bộ test. Ba ca vẫn khớp nhau không hở không chồng ở cả hai ngày |
| 2026-08-30 | `26c752b` | ★ **Test đỏ khi hoàn nguyên về cách tính sai** | **8 / 564** | Lab phá hoại C03: thay `ProductionCalendar` bằng bản `BaseUtcOffset` + trừ `DayStart` trên UTC. 7 đỏ trong `ProductionCalendarDaylightSavingTests`, 1 là `D4` ở `DE1`. Cây làm việc C03, chưa commit |
| 2026-08-30 | `26c752b` | Test `NV1` vẫn xanh trong cùng lần chạy lab | **tất cả** | Cùng lần chạy. `D4` ở `NV1` và đối chứng D3 ở `NV1` không phát hiện được lỗi — một site không DST không chứng minh gì cho site kia |
| 2026-08-30 | `26c752b` | Số test D3 **không** bắt được lỗi ở bản đầu | **3 / 8** | Cùng lần chạy lab, trước khi siết. Bản sai cho ba ca 8 giờ **cố định** nên chúng vẫn khớp nhau và vẫn chứa instant ở giữa. Phải thêm khẳng định **tổng độ dài production day** (23/25 giờ) và **giờ đầu/cuối của ca** mới đỏ |
| 2026-08-30 | working tree C06 | ★ **Retention theo `device_timestamp` xoá sentinel có đồng hồ chậm 500 ngày** | telemetry **1 → 0** · claim **1 → 1** · replay **0 inserted** | `make telemetry-policy-lab`, stack thật `timescale/timescaledb:2.29.2-pg17`. Sentinel nằm trong chunk riêng; gọi đúng `policy_retention` job. Claim toàn cục còn lại nên replay cùng id không thể dựng lại telemetry |
| 2026-08-30 | working tree C06 | Ghi muộn cặp 1 — control / chunk đã nén, **10.000 row** | **527,895 / 597,392 ms** · **1,132×** | `make telemetry-policy-lab`; hai chunk độc lập preload cùng **50.001 row**. Treatment **21.430.272 → 1.572.864 byte** trước khi timing |
| 2026-08-30 | working tree C06 | Ghi muộn cặp 2 — control / chunk đã nén, **10.000 row** | **513,171 / 548,761 ms** · **1,069×** | Cùng target. Treatment **21.479.424 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Ghi muộn cặp 3 — control / chunk đã nén, **10.000 row** | **752,896 / 1.128,912 ms** · **1,499×** | Cùng target. Treatment **21.463.040 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Ghi muộn cặp 4 — control / chunk đã nén, **10.000 row** | **480,148 / 597,720 ms** · **1,245×** | Cùng target. Treatment **21.454.848 → 1.564.672 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Ghi muộn cặp 5 — control / chunk đã nén, **10.000 row** | **580,034 / 571,942 ms** · **0,986×** | Cùng target. Treatment **21.430.272 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Tóm tắt 5 cặp độc lập — giá ghi muộn vào chunk đã nén | median của 5 pair ratio **1,132×** | Hai median biên **527,895 / 597,392 ms**. Mỗi trial có control/treatment riêng; thứ tự ghi xen kẽ. Chậm hơn **13,2 %** là số của local stack/workload này, không phải hằng số TimescaleDB |
| 2026-08-30 | working tree C06 | Chạy lại final, cặp 1 — control / chunk đã nén, **10.000 row** | **643,808 / 735,849 ms** · **1,143×** | Bản script có `SHARE` lock bảo vệ retention sentinel. Treatment **21.413.888 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Chạy lại final, cặp 2 — control / chunk đã nén, **10.000 row** | **823,116 / 763,540 ms** · **0,928×** | Cùng target final. Treatment **21.446.656 → 1.581.056 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Chạy lại final, cặp 3 — control / chunk đã nén, **10.000 row** | **818,646 / 845,827 ms** · **1,033×** | Cùng target final. Treatment **21.438.464 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Chạy lại final, cặp 4 — control / chunk đã nén, **10.000 row** | **627,207 / 652,482 ms** · **1,040×** | Cùng target final. Treatment **21.430.272 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Chạy lại final, cặp 5 — control / chunk đã nén, **10.000 row** | **582,866 / 660,250 ms** · **1,133×** | Cùng target final. Treatment **21.512.192 → 1.572.864 byte**, trạng thái `Compressed`, đúng **50.001 row** trước timing |
| 2026-08-30 | working tree C06 | Tóm tắt lần chạy final — 5 cặp độc lập | median của 5 pair ratio **1,040×** | Hai median biên **643,808 / 735,849 ms**; không dùng ratio của hai median làm paired estimator. Retention cùng run: giữ `SHARE` lock từ check chunk trống tới `run_job`, đếm chunk đúng **1 row**, rồi telemetry **1 → 0** |
| 2026-08-31 | `d8cd023` | C08 — dataset chuẩn 8 kênh × 7 ngày | **612.482 row / 62,096 s / 9.863 row/s** | `make telemetry-backfill CHANNELS=8 DAYS=7 SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0.25 END_AT=2026-08-22T00:00:00Z`; RBE từ đúng `FormationLine`/`FormationProfile`, không đi qua MQTT/gateway. Claim **612.482**, telemetry **612.482**, `Good` **459.293**, `Drifted` **153.189** |
| 2026-08-31 | `d8cd023` | C08 — storage của dataset chuẩn 8 kênh | database **1.357.706.931 → 1.746.319.027 byte** · delta **388.612.096 byte** | Cùng lượt trên; disk free **990.513.946.624 → 990.125.473.792 byte**. Compression policy nền vẫn bật, nên đây là chốt an toàn dung lượng toàn database, không phải tỉ số nén D1 |
| 2026-08-31 | `d8cd023` | C08 — chạy lại nguyên dataset chuẩn | **0 inserted / 612.482 duplicate** | Cùng tham số; `claims_verified = telemetry_verified = 612.482`, database chỉ đổi **49.152 byte** do metadata/background work, không có row logic mới |
| 2026-08-31 | `d8cd023` | C08 — `processed_message` triệu mới thứ 1 | **10,477296 s · 95.444,475 row/s** | `CHANNELS=40 DAYS=7 SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0.25 END_AT=2026-08-08T00:00:00Z`; binary `COPY` riêng phase claim. Global table bắt đầu lượt ở **6.127.588 key** |
| 2026-08-31 | `d8cd023` | C08 — `processed_message` triệu mới thứ 2 | **11,192841 s · 89.342,824 row/s** | Cùng process và transaction shape; index đã nhận thêm đúng 1 triệu key kể từ dòng trước |
| 2026-08-31 | `d8cd023` | C08 — `processed_message` triệu mới thứ 3 | **10,821686 s · 92.407,046 row/s** | Cùng process; ba điểm **không giảm đơn điệu**. Thấp nhất thấp hơn triệu đầu **6,39 %**; xem `ADR-030` §Evidence |
| 2026-08-31 | `d8cd023` | C08 — phase `COPY` telemetry của ba triệu liên tiếp | **21.769,311 / 21.388,285 / 21.895,549 row/s** | Cùng ba interval; ghi thẳng hypertable bằng binary `COPY`, sau khi claim của interval đã nằm trong cùng transaction |
| 2026-08-31 | `d8cd023` | C08 — tổng lượt 40 kênh có đồng hồ phase | **3.063.893 row / 883,728 s** | Claim `COPY` tổng **33,219 s** (**92.232 row/s**); telemetry `COPY` **142,580 s** (**21.489 row/s**). End-to-end còn gồm sinh RBE, lookup, verify và commit; không dùng nó để quy lỗi cho global index |
| 2026-08-31 | `d8cd023` | C08 — storage lượt 40 kênh có đồng hồ phase | database **3.611.768.499 → 5.452.519.091 byte** · delta **1.840.750.592 byte** | Disk free **986.574.229.504 → 984.145.895.424 byte**; cùng dataset 40 kênh × 7 ngày × 5 s, không qua MQTT/gateway. Policy nền vẫn bật, nên không dùng delta này làm kích thước chưa nén |
| 2026-08-31 | `d8cd023` | C08 — chunk bound của duplicate verifier | **13,350 → 1,278 s** cho **5.470 duplicate** | Cùng dataset 2 kênh × 1 ngày × 60 s trên database **9.191.473 telemetry**. Thêm min/max `device_timestamp` tường minh cho chunk exclusion; cả hai lần đều verify đủ **5.470** claim và telemetry, `COPY` cả hai bảng = **0 row** |
| 2026-08-31 | `d8cd023` | C08 — smoke code cuối, lượt mới rồi replay | lượt mới **5.470 inserted** · replay **0 inserted** | `END_AT=2026-07-31T00:00:00Z`, 2 kênh × 1 ngày × 60 s. Lượt mới: claim/telemetry đều **5.470**; replay: duplicate/claim/telemetry đều **5.470**, database delta **0 byte** |
| 2026-08-31 | `0aa5a34` | ★ C09/D1 — nén fixture **8 kênh** | **161.579.008 → 13.549.568 byte · còn 8,385723 %** | `make compression-report`; `NV1`, **437.798 row**, cardinality **48**, **5 chunk × 1 ngày**, chu kỳ mẫu **5 s**. Dữ liệu do C08 backfill từ đường cong thật sinh ra, **không qua MQTT/gateway**; rowstore được chuẩn hoá qua `decompress_chunk` trước khi đo |
| 2026-08-31 | `0aa5a34` | ★ C09/D1 — nén fixture **40 kênh** | **778.739.712 → 63.389.696 byte · còn 8,140036 %** | Cùng target; `NV1`, **2.186.459 row**, cardinality **240**, **5 chunk × 1 ngày**, chu kỳ mẫu **5 s**; cùng process phase và fault clock với fixture 8 kênh, không qua MQTT/gateway |
| 2026-08-31 | `0aa5a34` | ★ C09/D1 — độ ổn định giữa hai cardinality | **lệch 0,245687 điểm phần trăm** | Gate strict: ratio lớn nhất **8,385723 % < 15 %**, độ lệch **0,245687 pp < 2 pp**. Chủ repo dự đoán nguyên văn **25 % / 25 % / lệch 5 pp**; hai dự đoán 25 % tự suy ra 0 pp nhưng vẫn giữ đúng câu trả lời thay vì sửa sau khi thấy số |
| 2026-08-31 | `0aa5a34` | C09 — lượt đầu, fixture 8 kênh chưa chuẩn hoá rowstore | **175.218.688 → 13.549.568 byte · 7,732947 %** | Fixture vừa do backfill/COPY tạo; target bản đầu chưa đưa chunk chưa nén qua vòng chuẩn hoá. Row và cardinality giống dòng D1, nhưng free/index page làm baseline vật lý lớn hơn |
| 2026-08-31 | `0aa5a34` | C09 — lượt đầu, fixture 40 kênh chưa chuẩn hoá rowstore | **843.776.000 → 63.389.696 byte · 7,512621 %** | Cùng lượt đầu và cùng điều kiện fixture 40 kênh của dòng D1; con số đẹp hơn do rowstore sau COPY còn bloat, không được chọn làm evidence chính |
| 2026-08-31 | `0aa5a34` | C09 — rerun thứ nhất từ chunk đã nén, fixture 8 kênh | **161.579.008 → 13.549.568 byte · 8,385723 %** | Target tự `decompress_chunk` rồi nén lại; đây là lần đầu thấy compact rowstore boundary. Kết quả trùng byte-for-byte với lượt final ở dòng D1 8 kênh |
| 2026-08-31 | `0aa5a34` | C09 — rerun thứ nhất từ chunk đã nén, fixture 40 kênh | **778.739.712 → 63.389.696 byte · 8,140036 %** | Cùng rerun; kết quả trùng byte-for-byte với lượt final ở dòng D1 40 kênh. Target cuối chuẩn hoá cả trạng thái ban đầu chưa nén về boundary này |
| 2026-08-31 | `0aa5a34` | C09 — đối chứng gate phải đỏ được | **2 / 2 nhánh đỏ đúng** | Cùng transaction, gọi đúng hàm gate ở biên bằng ngưỡng: `ratio_equality` và `difference_equality` đều ném SQLSTATE riêng; gate thật sau đó xanh, commit giữ nguyên **437.798 / 2.186.459 row** |
| 2026-08-31 | `5221785` | C10/D2 — fingerprint fixture 100 kênh | **121.429 mẫu · 100 kênh · 10.080 bucket** | `make rollup-bench`; `NV1`, `Formation/Temperature`, `[2026-07-20, 2026-07-27)`, chu kỳ nguồn 5 s, `DriftedRate=0`; `CH-0001` có **1.172 mẫu / 965 bucket**; mẫu đầu/cuối `00:00:00` / `23:59:50` |
| 2026-08-31 | `5221785` | C10 — đối chiếu raw/rollup, một kênh | **965 = 965 bucket · 1.172 = 1.172 mẫu · delta 0** | Cùng target; `FULL JOIN` từng bucket, kiểm sample count và tổng có trọng số; missing/extra/mismatch đều **0** |
| 2026-08-31 | `5221785` | C10 — đối chiếu raw/rollup, cả `FORM-01` | **10.080 = 10.080 bucket · 121.429 = 121.429 mẫu · delta 0** | Cùng target; trung bình máy dùng `sum(avg_value * sample_count) / sum(sample_count)`, không dùng trung bình của các trung bình kênh |
| 2026-08-31 | `5221785` | C10 trial 01 — rollup, một kênh | **29,627 ms** | `make rollup-bench`; server clock, sau 1 warmup, thứ tự 1/4; query trả checksum cố định |
| 2026-08-31 | `5221785` | C10 trial 01 — rollup, 100 kênh | **109,598 ms** | Cùng lượt; thứ tự 2/4, cùng fixture và cửa sổ 7 ngày |
| 2026-08-31 | `5221785` | C10 trial 01 — raw, một kênh | **4,720 ms** | Cùng lượt; thứ tự 3/4, cùng checksum với rollup một kênh |
| 2026-08-31 | `5221785` | C10 trial 01 — raw, 100 kênh | **283,507 ms** | Cùng lượt; thứ tự 4/4, cùng checksum với rollup cả máy |
| 2026-08-31 | `5221785` | C10 trial 02 — rollup, 100 kênh | **144,061 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 02 — raw, một kênh | **5,701 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 02 — raw, 100 kênh | **262,673 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 02 — rollup, một kênh | **26,328 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 03 — raw, một kênh | **5,300 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 03 — raw, 100 kênh | **256,221 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 03 — rollup, một kênh | **23,987 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 03 — rollup, 100 kênh | **103,382 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 04 — raw, 100 kênh | **263,545 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 04 — rollup, một kênh | **25,479 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 04 — rollup, 100 kênh | **105,503 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 04 — raw, một kênh | **4,685 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 05 — rollup, một kênh | **23,503 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 05 — rollup, 100 kênh | **109,411 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 05 — raw, một kênh | **4,662 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 05 — raw, 100 kênh | **251,195 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 06 — rollup, 100 kênh | **103,438 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 06 — raw, một kênh | **4,354 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 06 — raw, 100 kênh | **261,047 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 06 — rollup, một kênh | **26,248 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 07 — raw, một kênh | **6,078 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 07 — raw, 100 kênh | **250,925 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 07 — rollup, một kênh | **22,855 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 07 — rollup, 100 kênh | **102,369 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 08 — raw, 100 kênh | **270,139 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 08 — rollup, một kênh | **24,506 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 08 — rollup, 100 kênh | **98,972 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 08 — raw, một kênh | **4,887 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 09 — rollup, một kênh | **23,690 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 09 — rollup, 100 kênh | **99,425 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 09 — raw, một kênh | **4,364 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 09 — raw, 100 kênh | **247,302 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | C10 trial 10 — rollup, 100 kênh | **99,570 ms** | Cùng benchmark; thứ tự 1/4 |
| 2026-08-31 | `5221785` | C10 trial 10 — raw, một kênh | **4,022 ms** | Cùng benchmark; thứ tự 2/4 |
| 2026-08-31 | `5221785` | C10 trial 10 — raw, 100 kênh | **239,024 ms** | Cùng benchmark; thứ tự 3/4 |
| 2026-08-31 | `5221785` | C10 trial 10 — rollup, một kênh | **23,157 ms** | Cùng benchmark; thứ tự 4/4 |
| 2026-08-31 | `5221785` | ★ C10/D2 — rollup một kênh, 10 trial | **p50 23,987 · p95/max 29,627 ms** | `percentile_disc`, nearest-rank; với 10 mẫu, p95 là mẫu chậm nhất; gate strict **29,627 < 200 ms** |
| 2026-08-31 | `5221785` | ★ C10/D2 — rollup cả máy 100 kênh, 10 trial | **p50 103,382 · p95/max 144,061 ms** | Cùng cách đo; gate strict **144,061 < 200 ms**. Chưa cần rollup tầng hai |
| 2026-08-31 | `5221785` | C10 control — raw một kênh, 10 trial | **p50 4,685 · p95/max 6,078 ms** | Raw là đối chứng, không gate. Ở cardinality một kênh, raw nhanh hơn rollup trên fixture đã nén và cache ấm |
| 2026-08-31 | `5221785` | C10 control — raw cả máy 100 kênh, 10 trial | **p50 256,221 · p95/max 283,507 ms** | Raw là đối chứng, không gate. Rollup giảm p95 máy từ **283,507** còn **144,061 ms** |
| 2026-08-31 | `5221785` | C10 — oracle chunk exclusion, hai query rollup | **2/2 materialization chunk · 0 ngoài cửa sổ · 0 raw chunk** | `EXPLAIN (ANALYZE, BUFFERS, VERBOSE, FORMAT JSON)`; map relation vật lý về logical chunk bằng catalog TimescaleDB; far control đã materialize |
| 2026-08-31 | `5221785` | C10 — oracle chunk exclusion, hai query raw | **7/7 raw chunk · 0 ngoài cửa sổ · 0 materialization chunk** | Cùng oracle; mỗi logical chunk đã nén có cả relation logic và relation compressed nên đối chiếu theo logical chunk id, không grep tên node |
| 2026-08-31 | `0694812` | C11 — baseline của probe dữ liệu đến muộn | **raw 0 · rollup 0 · delta 0** | `make rollup-reconcile`; `NV1`, equipment UUID riêng, `Formation/Temperature`, phút `[2026-08-30T21:33Z, 21:34Z)`; forced refresh trước khi append để baseline vừa được materialize |
| 2026-08-31 | `0694812` | C11/D5 — ngay sau khi append dữ liệu đến muộn | **raw 3 · rollup 0 · delta 3** | Cùng lượt; append atomically đúng **3 claim + 3 telemetry**, `device_timestamp` cũ 6 giờ, không `UPDATE`/`DELETE`/cleanup |
| 2026-08-31 | `0694812` | C11/D5 — sau refresh policy thật | **raw 3 · rollup 0 · delta 3** | Scheduler chạy job động `1006`: `total_runs +1`, `total_successes +1`, `total_failures +0`, status `Success`; mẫu nằm ngoài `start_offset=5h` nên policy không sửa bucket cũ |
| 2026-08-31 | `0694812` | ★ C11/D5 — sau bounded wide refresh đúng một phút | **raw 3 · rollup 3 · delta 0** | Cùng lượt; `CALL refresh_continuous_aggregate` trên chính khoảng đóng, `force=false`; phép đối chiếu cuối xanh |
| 2026-08-31 | `0694812` | C11 — đối chứng phép đối chiếu phải đỏ được | **1 mismatch đúng SQLSTATE `P1101`** | Gọi checker khi delta còn **3**; chỉ bắt SQLSTATE riêng, mọi lỗi SQL khác rethrow. Sau bounded refresh, cùng checker xanh |
| 2026-08-31 | `0694812` | C11 — foreground `run_job` và `job_stats` | **652 → 652 run · 652 → 652 success** | Phép kiểm riêng trên TimescaleDB 2.29.2: `CALL run_job(1006)` chạy xong nhưng không tăng stats. Harness vì thế chờ scheduler thật, không gán nhầm bằng chứng cho foreground call |
| 2026-08-31 | working tree C14 | ★ C14 — ngày dương lịch gán sai `production_day` trên dữ liệu **thật** | **30.334 / 121.429 row = 24,980853 %** | `make calendar-lab`; fixture C10 đã khóa: `NV1`, 100 kênh `Formation/Temperature`, `[2026-07-20, 2026-07-27)`, toàn bộ `clock_quality=Good`. PostgreSQL session lấy `Asia/Ho_Chi_Minh` từ factory model revision 3 rồi chạy đúng `CAST(device_timestamp AS date)` |
| 2026-08-31 | working tree C14 | C14 — đối chứng ca C ngày thường ở `DE1` | **360 / 480 row = 75,000000 %** | Dữ liệu tổng hợp bằng code lịch, một row mỗi phút trôi qua; `production_day=2026-02-14`, ca thật 8 giờ. Vế này tách phần sai 00:00–06:00 khỏi hiệu ứng DST |
| 2026-08-31 | working tree C14 | C14 — ca C qua DST mùa xuân ở `DE1` | **300 / 420 row = 71,428571 %** | Dữ liệu tổng hợp; `production_day=2026-03-28`, ca thật **7 giờ**. So với ngày thường: **−3,571429 điểm phần trăm** |
| 2026-08-31 | working tree C14 | C14 — ca C qua DST mùa thu ở `DE1` | **420 / 540 row = 77,777778 %** | Dữ liệu tổng hợp; `production_day=2026-10-24`, ca thật **9 giờ**. So với ngày thường: **+2,777778 điểm phần trăm** |
| 2026-08-31 | working tree C14 | C14 — độ nhạy của harness | **1 fixture thật + 3 fixture tổng hợp qua gate · 3/3 unit test xanh** | Target từ chối fingerprint C10 lệch, phép kiểm rỗng, ca DST không còn 7/9 giờ, hoặc cả bốn phép so sánh không tìm thấy row sai. `CAST` vẫn trả ngày hợp lệ và không ném lỗi — chính là hình dạng lỗi lab phải phơi ra |
| 2026-08-31 | `M3/C15` | C15-0 preflight — dung lượng trống của volume Postgres **trước** khi sinh dữ liệu D1b | **980.020.314.112 byte trống / 1.081.101.176.832 byte tổng (4 % đã dùng)** | `df -B1 /var/lib/postgresql/data` trong `nvm-timescale`. Đo **trước** theo `R-M3-7`; con số sau lượt sinh ghi thành dòng riêng |
| 2026-08-31 | `M3/C15` | C15-0 preflight — kích thước database hiện tại | **5.704.505.011 byte** | `pg_database_size(current_database())`. Trong đó hypertable telemetry **1.402.511.360** và `ingest.processed_message` **3.714.981.888** |
| 2026-08-31 | `M3/C15` | ★ C15-0 preflight — **giá lưu trữ của một row telemetry so với claim của chính nó** | **83,2 byte** so với **220,4 byte** | 16.857.420 row telemetry (52/61 chunk đã nén) so với 16.857.428 row `ingest.processed_message` (bảng thường, PK UUID ngẫu nhiên). Bảng claim tốn **2,65×** bảng nó bảo vệ — đây là cái giá của `ADR-030` đo bằng byte, cạnh cái giá đo bằng thời gian mà C08 đã ghi |
| 2026-08-31 | `M3/C15` | C15-0 preflight — giá một row telemetry **chưa nén** | **404,6 byte/row** | Trung bình 5 chunk rowstore đầy ngày (`2026-08-24`…`2026-08-28`), 8 kênh: 35.028.992…35.323.904 byte cho 86.581…89.967 row. Dùng để ước lượng đỉnh đĩa của lượt sinh D1b |
| 2026-08-31 | `M3/C15` | C15-0 preflight — mật độ row thật ở cardinality 40 kênh | **437.699 row/ngày** | Suy ra từ chính lượt C08 đã ghi: 3.063.893 row cho 7 ngày. Bảng ngày trong DB xác nhận: `2026-08-01`…`2026-08-14` dao động 433.621…439.613 row/ngày với đúng 40 kênh / 240 series |
| 2026-08-31 | `M3/C15` | C15-0 preflight — cửa sổ ngày còn trống trước dữ liệu cũ nhất | **`device_timestamp` nhỏ nhất = 2026-07-03T12:00:00,001Z** | `min(device_timestamp)` toàn hypertable. Mọi ngày ≤ `2026-07-02` chưa có row nào, nên hai cửa sổ D1b đặt được ở đó mà không chạm fixture C09 (`2026-08-02…07`, `2026-08-16…21`) lẫn fixture C10 (`2026-07-20…27`) |
| 2026-08-31 | `M3/C15` | C15-0 preflight — job đang chạy trên hypertable telemetry | **0 `policy_retention` · 1 `policy_compression` (`compress_after` 7 ngày, mỗi 12 giờ) · 1 refresh CAGG (`start_offset` 5 giờ)** | `timescaledb_information.jobs`. Xác nhận lại bản sửa R1: không job xoá nào còn sống, nên dữ liệu tháng 5–6/2026 sinh mới sẽ không bị dọn giữa chừng |
| 2026-08-31 | `M3/C15` | C15-2/D1b — sinh bậc **nhỏ**: 40 kênh × 9 ngày | **3.939.289 row / 338,866 s / 11.624,931 row/s** | `make telemetry-backfill CHANNELS=40 DAYS=9 END_AT=2026-06-01T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0.25 CLOCK_DRIFT_HOURS=2`. Claim `COPY` **38,781 s** (101.579 row/s), telemetry `COPY` **205,459 s** (19.173 row/s). `Good` 2.757.310 · `Drifted` 1.181.979 · duplicate **0** · verified 3.939.289/3.939.289 |
| 2026-08-31 | `M3/C15` | C15-2/D1b — sinh bậc **lớn**: 40 kênh × 30 ngày | **13.130.541 row / 1.388,577 s / 9.456,115 row/s** | Cùng bộ cờ, `DAYS=30 END_AT=2026-07-02T00:00:00Z`. Claim `COPY` **126,223 s** (104.026 row/s), telemetry `COPY` **615,034 s** (21.349 row/s). `Good` 9.190.730 · `Drifted` 3.939.811 · duplicate **0** |
| 2026-08-31 | `M3/C15` | C15-2 — đĩa và database sau **cả hai** lượt sinh | **trống 966.792.048.640 byte · database 16.062.396.083 byte** | Trước lượt nhỏ: trống 980.019.998.720 · database 5.704.545.971. Delta database **+10,36 GB** cho 17.069.830 row = **607 byte/row** gồm cả claim, index và WAL. Preflight ngoại suy 625 byte/row — lệch **2,9 %** |
| 2026-08-31 | `M3/C15` | ★★ C15-2 → `ADR-030` — **phần chậm dần KHÔNG nằm ở btree UUID toàn cục** | end-to-end **95,270 → 126,817 s/triệu (+33,1 %)**, trong khi claim `COPY` **11,098 → 10,189 s/triệu** và telemetry `COPY` **48,789 → 46,110 s/triệu** | 13 mốc triệu liên tiếp của lượt 30 ngày; bảng claim lớn từ ~20,8 lên **34,0 triệu** key trong chính lượt này. Hai phase `COPY` **phẳng hoặc tốt lên** (claim chạm đáy 8,730 s ở mốc 12); phần dư ngoài `COPY` đi từ **35,38 → 70,52 s/triệu**, tức **gấp đôi**. Kết luận: ở quy mô này, thứ suy giảm là phase sinh/lookup/verify/commit, **không** phải insert vào btree ngẫu nhiên — đúng điều `ADR-030` lo, đo được là **chưa xảy ra** |
| 2026-08-31 | `M3/C15` | C15-2 — sai số của chính ngoại suy preflight | **288 s/triệu ngoại suy so với 86,0 và 105,8 s/triệu đo được** | Preflight suy từ dòng C08 *"3.063.893 row / 883,728 s"*. Lượt nhỏ chạy nhanh hơn **3,3×**, lượt lớn nhanh hơn **2,7×**. Ghi lại vì nó nói ngoại suy từ **một** dòng tổng end-to-end là ngoại suy yếu — dòng đó chở theo cả điều kiện máy lúc ấy |
| 2026-08-31 | `M3/C15` | C15-2 — fingerprint cửa sổ đo bậc **nhỏ** `[2026-05-24, 2026-05-31)` | **3.062.323 row · 40 kênh · cardinality 240 · 7 chunk** | Mẫu đầu `2026-05-24T00:00:00Z`, mẫu cuối `2026-05-30T23:59:55Z`, row site khác **0**. Đủ cả 7 assertion mà `compression-report.sql` áp cho một fixture |
| 2026-08-31 | `M3/C15` | C15-2 — fingerprint cửa sổ đo bậc **lớn** `[2026-06-03, 2026-07-01)` | **12.253.575 row · 40 kênh · cardinality 240 · 28 chunk** | Mẫu đầu `2026-06-03T00:00:00Z`, mẫu cuối `2026-06-30T23:59:55Z`, row site khác **0** |
| 2026-08-31 | `M3/C15` | ★ C15-2 — bội số dung lượng thật của trục thứ hai | **12.253.575 / 3.062.323 = 4,001404×** | Cùng cardinality 40 kênh, cùng chu kỳ 5 s, cùng `DRIFTED_RATE=0.25`. Ngưỡng `ADR-034` D1 điều kiện (3): **≥ 4×** |
| 2026-08-31 | working tree C15-2 | ★★ **C15-2/D1 — trục DUNG LƯỢNG: 40 kênh × 7 ngày** | **1.089.871.872 → 88.793.088 byte · còn 8,147113 %** | `make compression-report`; `NV1`, **3.062.323 row**, cardinality **240**, **7 chunk × 1 ngày**, chu kỳ **5 s**, `DRIFTED_RATE=0.25`. Sinh bằng đúng code đường cong simulator, **không qua MQTT/gateway**; rowstore chuẩn hoá qua `decompress_chunk` trước khi đo |
| 2026-08-31 | working tree C15-2 | ★★ **C15-2/D1 — trục DUNG LƯỢNG: 40 kênh × 28 ngày** | **4.358.291.456 → 355.262.464 byte · còn 8,151416 %** | Cùng lượt, cùng cardinality 240, **28 chunk**, **12.253.575 row** = **4,001399×** bậc nhỏ. Mọi điều kiện dữ liệu giữ nguyên |
| 2026-08-31 | working tree C15-2 | ★★★ **D1 — độ ổn định của tỉ số nén trên HAI trục** | trục dung lượng **0,004303 pp** · trục cardinality **0,245687 pp** · spread toàn tập **0,245687 pp** | Ngưỡng `ADR-034`: mọi tỉ số < 15 %, mọi lệch < 2 pp, bậc lớn ≥ 4×. Bốn tỉ số: 8,385723 / 8,140036 / 8,147113 / 8,151416 %. **Gate xanh.** Con số đáng nhớ không phải "đạt" mà là **tỉ lệ giữa hai trục: 57×** — nhân bốn số dòng lên gần như không đổi gì, đổi cardinality thì đổi thật |
| 2026-08-31 | working tree C15-2 | ★ D1 — **dự đoán so với thực đo** | dự đoán **< 0,5 pp**, đo được **0,004303 pp** | Dự đoán của chủ repo ghi ở commit `M3/C15`, **trước** commit chứa số đo. Nhỏ hơn ngưỡng đã chọn **116 lần**. Mệnh đề plan §2.4 — *"tỉ số nén là tính chất của hình dạng dữ liệu, không phải của số dòng"* — nay có bằng chứng trên chính trục nó đang bác bỏ |
| 2026-08-31 | working tree C15-2 | C15-2 — đối chứng gate 4 scenario phải đỏ được | **5 / 5 nhánh đỏ đúng** | Mỗi nhánh ép ở biên bằng chính giá trị quan sát được, các ngưỡng còn lại để rộng: `ratio_equality` (P3091) · `overall_difference_equality` (P3092) · `cardinality_axis_equality` (P3093) · `volume_axis_equality` (P3094) · `volume_multiple` (P3095). Gate cũ có 2 nhánh / 2 đối chứng |
| 2026-08-31 | working tree K13 | ★ **Healthcheck `nvm-timescale` hết ngân sách khi database lớn lên** | **141 s** để healthy, ngân sách cũ **20 s + 20×5 s** | `StartedAt 09:51:10.790Z` → health log xanh đầu tiên `09:53:31.413Z`. Database **16.062.396.083 byte**, **103 chunk** (trước lượt D1: 5,70 GB / 61 chunk). `make up-obs` báo *"container nvm-timescale is unhealthy"* và **bỏ Grafana lại**, trong khi Postgres bình thường vài giây sau. `start_period` nâng 20 s → **180 s** |
| 2026-08-31 | working tree K13 | ★★ **K13 runtime — 13 phép kiểm chứng sau khi xoay 9 credential** | **13/13 PASS** | `sh scripts/rotate-verify.sh`. 9 phép đăng nhập thật bằng giá trị mới; 1 phép K3 (`nvm_grafana` đọc thẳng `ts` phải **bị từ chối**); **3 phép đối chiếu giá trị CŨ đọc từ `.env.rotate-backup` phải bị từ chối** — Postgres, MSSQL `sa`, EMQX. Vế thứ ba mới là vế trả lời câu hỏi của K13; `make secret-check` chỉ đọc `.env` |
| 2026-08-31 | working tree K13 | K13 — ba gate cũ sau khi xoay | `secret-check` **OK** · `net-check` **9/9** · `grafana-net-check` **3/3** | Chạy sau `make down && make up-obs && make ingestion-up`. Không dùng `down -v`; dataset 33,9 triệu row và bucket `raw-curve` dưới object lock giữ nguyên |
| 2026-08-31 | working tree K13 | K13 — bẫy đo lường của chính phép kiểm | `MSYS_NO_PATHCONV=1` làm `curl -o /dev/null` trả **exit 23** | Biến này cần cho `docker exec ... /opt/mssql-tools18/bin/sqlcmd`, nhưng nó tắt luôn phép dịch `/dev/null` → `NUL` của Git Bash. Hai phép kiểm Keycloak và Grafana báo **FAIL** hai lần liên tiếp trong khi credential hoàn toàn đúng — tái lập được bằng cách chạy cùng lệnh có và không có biến đó |
| 2026-09-01 | `M3/C15` | C15-3 — fixture line, và **lượt sinh chết ở 68/77 triệu row** | **6 ngày trọn vẹn** `[2026-05-13, 2026-05-19)` · 1.000 kênh · 10 cycler · 1.040.949 row `Formation/Temperature` | `CHANNELS=1000 DAYS=7 END_AT=2026-05-20T00:00:00Z SAMPLE_PERIOD_SECONDS=5 DRIFTED_RATE=0`. Chết sau **6 giờ** với `Exception while reading from stream`; Postgres log `unexpected EOF on client connection with an open transaction`, `RestartCount=0`, không OOM ⇒ **client** rời mạng, server nguyên. Đo trên 6 ngày trọn thay vì chạy lại 6 giờ |
| 2026-09-01 | `M3/C15` | C15-3 — đối chiếu raw vs rollup ở mức line | **8.640 = 8.640 bucket · 1.040.949 = 1.040.949 mẫu · lệch 0 mọi vế** | `rollup_only=0 raw_only=0 sample_mismatch=0 value_mismatch=0`. Điều kiện phải có trước khi đo: một benchmark nhanh trên rollup sai là benchmark vô nghĩa |
| 2026-09-01 | working tree C15-3 | ★★★ **C15-3/D2 evidence — truy vấn line `F1` 1.000 kênh, 6 ngày** | **p95 16.802,894 ms** · p50 12.751,808 · min 7.374,532 | `make rollup-bench-line`. Ngưỡng D2 là **200 ms** ⇒ **NỢ**, deadline **trước dashboard line-wide M6/M7** (`ADR-034`). 10 lần đo + 1 warmup, đan xen scenario, p95 nearest-rank |
| 2026-09-01 | working tree C15-3 | ★★★ **C15-3 — `FORM-01` 100 kênh đo LẠI trong cửa sổ có cả line** | **p95 14.758,431 ms**, so với **144,061 ms** của C10 | **Cùng truy vấn, cùng scope 100 kênh.** Khác biệt duy nhất: cửa sổ của C10 chỉ chứa 100 kênh, cửa sổ này chứa cả 1.000. Chênh **102×**. D2 **không** bị bác bỏ — nhưng bằng chứng của nó **không** suy rộng sang nhà máy có đủ 10 cycler trong cùng khoảng thời gian |
| 2026-09-01 | working tree C15-3 | ★★ **C15-3 — giá của cardinality gần bằng KHÔNG, và đó mới là vấn đề** | 10× số kênh → **1,14×** thời gian (14.758 → 16.803 ms) | Một truy vấn quét mà nhân 10 lần dữ liệu chỉ chậm 14 % nghĩa là nó **đã đọc gần hết** ngay từ đầu. `EXPLAIN`: index của rollup là `(signal_code, bucket)`, `equipment_id` nằm ở **`Filter`** chứ không phải `Index Cond` |
| 2026-09-01 | working tree C15-3 | ★ C15-3 — ba nguyên nhân, tách bằng số | đọc **265.140 block = 2,07 GB** (`shared hit=2`) · bitmap **lossy** `exact=14713 lossy=36613` · recheck **2.320.136** row · `Filter` loại **171.882** row | `EXPLAIN (ANALYZE, BUFFERS)` trên truy vấn mức máy. `work_mem` mặc định 4 MB không đủ giữ bitmap chính xác |
| 2026-09-01 | working tree C15-3 | C15-3 — `work_mem` 4 MB so với 256 MB trên cùng truy vấn | **14.758 ms** so với **9.328 / 11.928 ms** | Nới `work_mem` xoá được bitmap lossy và lấy lại ~1/3 thời gian. Hai phần ba còn lại là 2,07 GB đọc đĩa cho dữ liệu bị vứt 90 % — `work_mem` không chữa được phần đó |
| 2026-09-01 | working tree C15-3 | C15-3 — rollup đáng giá bao nhiêu ở mức line | rollup p95 **16.802,894 ms** so với raw p95 **27.229,932 ms** = **1,62×** | Ở mức máy trong C10 tỉ số này là ~1,8×. Rollup vẫn thắng, nhưng thắng **ít hơn nhiều** so với kỳ vọng vì cả hai vế đều đang đọc thừa |
| 2026-09-01 | working tree C15-3 | ★ **`/dev/shm` 64 MB làm truy vấn song song chết ngẫu nhiên** | **1 / 12 lần chạy** đỏ với `could not resize shared memory segment ... No space left on device` | Mặc định của Docker; `docker-compose.yml` chưa khai `shm_size`. Đã đặt **1 GB**. Lỗi này không phụ thuộc SQL — nó phụ thuộc có bao nhiêu parallel worker xin bộ nhớ cùng lúc, nên một dashboard Grafana mức line sẽ gặp đúng nó |
| 2026-09-01 | working tree C15-3 | C15-3 — checksum không ổn định do cộng song song | **8/11** lần `2967929.682969960`, **3/11** lần `...950` — lệch **1e-8** trên 2,97e6 = **3,4e-15**, đúng một ULP | `EXPLAIN` xác nhận `Gather / Workers Planned: 2 / Parallel Append`. Fingerprint đổi từ làm tròn 9 xuống **6** chữ số: biên an toàn 100× so với nhiễu, trong khi một mẫu nhiệt độ thiếu sẽ đổi tổng khoảng **800** đơn vị |
| 2026-09-01 | `M3/C15` | C15 — `make ci`: build và test | **0 warning · 0 error · 679/679 test xanh** trong **2 phút 24 s** | `dotnet format --verify-no-changes` sạch; Release build 29,13 s; 5 assembly test, 0 fail, 0 skip. M2 kết thúc ở **585** → **+94** test |
| 2026-09-01 | `M3/C15` | C15 — `make buffer-crash` 200 vòng | **200/200 vòng, 0 đỏ, `truncated=0`** | Mỗi vòng SIGKILL rồi reopen bằng process mới. Chạy `scripts/buffer-crash.sh` trực tiếp trên image `nvm-edge-gateway:dev` có sẵn, **bỏ bước `docker compose build`** vì lý do dòng dưới. `git diff 022af32..HEAD -- '*.cs'` = **0 file**, nên image khớp code |
| 2026-09-01 | `M3/C15` | ★ **`make ci` KHÔNG xanh trọn vẹn — mạng, không phải code** | `api.nuget.org` **HTTP 000 sau 300 s** từ host **và** từ container | `docker compose build edge-gateway` trong `make ci` chết vì `dotnet restore` không tải được service index (`NU1900` bị `Warning As Error`). Cùng lúc: `google.com` **200** (0,89 s), `github.com` **200** (1,62 s), image `curlimages/curl` tải về bình thường. Hỏng đúng một host. Ô `make ci` trong checklist M3 **giữ nguyên chưa tick** |
| 2026-09-01 | `M3/C15` | ★★★ **D2 đo lại trên database đã lớn — TRƯỢT** | `rollup_machine` **p50 124,796 ms · p95 201,547 ms** · `gate=fail`, ngưỡng 200 | `make rollup-bench`, **cùng fixture C10 đã ghim**, fingerprint đạt, plan oracle đạt (đọc đúng 2 chunk CAGG, `non_target_chunks_read=0`). Khác biệt duy nhất so với lần đo `5221785`: database đi từ **5,70 → ~28 GB** vì fixture evidence của C15-3, còn `shared_buffers` vẫn **983 MB**. p95 nearest-rank trên 10 mẫu = mẫu **tệ nhất**, nên một lần chạy 201,5 ms lật gate |
| 2026-09-01 | `M3/C15` | D2 đo lại — ba scenario còn lại | `rollup_channel` p95 **40,416 ms** · `raw_channel` p95 **11,243 ms** · `raw_machine` p95 **402,934 ms** | Cùng lượt. Mức kênh vẫn thoải mái; mức máy trên bảng thô **402,934 ms** so với **283,507 ms** ở lần đo trước — cả hai đường đều chậm đi, đúng dấu hiệu áp lực cache chứ không phải một truy vấn nào đó thoái hoá riêng |
| 2026-09-01 | working tree D2-fix | ★★ **Nguyên nhân thật của D2 trượt — bố trí vật lý, không phải index** | Bitmap Index Scan trả **99.587 row** · `Filter` loại **0 row** · `Heap Blocks: exact` **33.400** | `EXPLAIN (ANALYZE, BUFFERS)` đầy đủ trên truy vấn mức máy của C10. **~3 row hữu ích mỗi trang 8 kB.** Index làm đúng việc — nó trả về đúng tập row cần. Materialized hypertable lưu theo thứ tự refresh nên trộn lẫn 1.000 kênh × 6 signal, đọc một chuỗi của một máy thì chạm gần hết trang của chunk |
| 2026-09-01 | working tree D2-fix | ★★ **Rollup là quan hệ DUY NHẤT trong đường đọc chưa được nén** | `compression_enabled=false`, **0/6** chunk | Trong khi bảng thô đã có `segmentby=(site_id, equipment_id, signal_code)` từ C06. Đây là thiếu sót của **C07**, không phải hệ quả của dữ liệu lớn — dữ liệu lớn chỉ gỡ tấm cache che nó |
| 2026-09-01 | working tree D2-fix | ★ Migration `011` — nén rollup, cùng `segmentby` với bảng thô | **4.621.721.600 → 483.287.040 byte · nhỏ đi 9,56×** | 6/6 chunk CAGG. `policy_retention` toàn schema vẫn **0** sau migration — nén không mang một job xoá nào quay lại |
| 2026-09-01 | working tree D2-fix | ★★★ **D2 sau khi nén rollup — XANH LẠI, biên 3,7 %** | `rollup_machine` **p50 135,887 · p95 192,560 ms** · `gate=pass` | `make rollup-bench`, cùng fixture C10 đã ghim. So với lần trượt: p95 **201,547 → 192,560** (−4,5 %) nhưng p50 **124,796 → 135,887** (**+8,9 %**). Nén **đổi trung vị lấy đuôi**: ít I/O hơn, thêm CPU giải nén |
| 2026-09-01 | working tree D2-fix | D2 sau khi nén — ba scenario còn lại | `rollup_channel` p95 **68,764 ms** · `raw_channel` **11,578 ms** · `raw_machine` **578,718 ms** | Cùng lượt. `raw_machine` đi từ 402,934 lên **578,718 ms** — bảng thô không được lợi gì từ việc nén rollup, và nó tiếp tục xấu đi theo dung lượng database. Đó là con số nói vì sao rollup tồn tại |
| 2026-09-01 | working tree D2-fix | ★★ **C15-3 đo lại sau khi nén rollup — line `F1` 1.000 kênh** | **p95 1.171,168 ms** · p50 567,683 · min 492,866 | Nhanh hơn **14,3×** so với 16.802,894 ms lúc rollup chưa nén. Vẫn **NỢ**: vượt ngưỡng 200 ms **5,9 lần**, thay vì 84 lần |
| 2026-09-01 | working tree D2-fix | C15-3 đo lại — `FORM-01` 100 kênh trong cửa sổ có cả line | **p95 1.312,404 ms** · p50 521,080 | Nhanh hơn **11,2×** so với 14.758,431 ms. Vẫn xa **192,560 ms** mà cùng truy vấn đạt trong cửa sổ chỉ có 100 kênh — khoảng cách còn **6,8×**, tức nguyên nhân `equipment_id` nằm ở `Filter` **chưa được chạm tới** |
| 2026-09-01 | working tree D2-fix | C15-3 đo lại — rollup so với thô ở mức line | rollup **1.171,168 ms** so với raw **2.385,827 ms** = **2,04×** | Trước khi nén tỉ số này là 1,62×. Nén rollup làm rollup đáng giá hơn, đúng như thiết kế mong đợi |
| 2026-09-01 | working tree D2-fix | C15-3 đo lại — giá của cardinality vẫn gần bằng không | 10× số kênh → **0,89×** thời gian (1.312 → 1.171 ms) | p50 thì line vẫn chậm hơn máy (567,7 so với 521,1 ms); hai p95 đảo nhau vì p95 là mẫu tệ nhất trong 10. Kết luận không đổi: truy vấn mức máy **vẫn đọc gần hết** dữ liệu của line |
| 2026-09-01 | working tree D2-fix | ★ **Test bắt được migration `011` ngay khi nó vào** | **2 / 679 test đỏ**, rồi **679/679 xanh** sau khi sửa | `ProcessSignalRollupTests` khẳng định **chính xác** danh sách job của rollup, nên thêm `policy_compression` làm nó đỏ — guard làm đúng việc. Test đỏ còn phơi ra một bug **của chính migration**: `Down/011` ghi cứng `_materialized_hypertable_4`, số riêng của máy này; container test đánh số khác nên rollback sẽ **giải nén 0 chunk mà vẫn báo thành công**. Nay tra qua catalogue |
| 2026-09-01 | working tree D2-fix | C15 — `make ci` chạy lại sau migration `011` | `format` sạch · build **0/0** · test **679/679** · `buffer-crash` **200/200** | Bước `docker compose build` vẫn không chạy được vì `api.nuget.org` chưa tới được. Ô `make ci` trong checklist M3 **vẫn chưa tick** |
| 2026-09-01 | working tree audit-2 | ★★★ **Audit độc lập — RabbitMQ: node hiện tại thấy 0 queue** | **5** node identity trong volume · node bỏ rơi `rabbit@9d6be807dc36` giữ **4,1 MB** · node hiện tại `rabbit@f2f556f0e82d` **196 K, 0 queue** | `rabbitmqctl list_queues` trả rỗng; `du -sh /var/lib/rabbitmq/mnesia/rabbit@*`. Compose không có `hostname` lẫn `RABBITMQ_NODENAME`. Tôi đã ghi cơ chế này vào `runbook.md` §1.1 như **lý do việc xoay chạy được**; thực chất nó là **mất state** |
| 2026-09-01 | working tree audit-2 | ★★ **Audit độc lập — chỉ 3/9 credential cũ được thử từ chối** | **3/9** | `rotate-verify.sh` dòng 139–141. Sáu cái chưa thử: `nvm_grafana`, `nvm_app`, RabbitMQ, MinIO, Keycloak, Grafana admin. Kết luận *"K13 đóng cả hai nửa, 13/13"* của tôi **vượt phạm vi bằng chứng** |
| 2026-09-01 | working tree audit-2 | ★ **`window_days=7` là một câu sai nằm trong output của chính phép đo** | in `7`, cửa sổ thật **6** ngày | Hằng số ghi cứng trong `NVM_LINE_VERDICT`. Đã sửa thành `extract(day FROM end_at - start_at)`; lượt chạy sau in `window_days=6` |
| 2026-09-01 | working tree audit-2 | C15-3 — lượt đo thứ ba của line, sau khi sửa `window_days` | **p95 828,135 ms** | Cùng fixture, cùng script. Ba lượt: **16.802,894** (rollup chưa nén) → **1.171,168** → **828,135 ms**. Phân tán lớn giữa hai lượt sau là do cache ấm dần; cả ba đều **NỢ** |
| 2026-09-01 | working tree audit-2 | Audit độc lập — hình dạng vật lý của rollup lúc đo | **6/6 chunk CAGG đang nén**, 0 chunk rowstore | Migration `011` cố ý để tuần mới nhất ở rowstore, nhưng mọi dữ liệu đều đã cũ nên không có tuần nào mới. Phép đo **chưa từng** chạm hình dạng thật của *"7 ngày qua"*: nóng ở đầu, nén ở đuôi |
| 2026-09-01 | working tree audit-2 | ★ **N-M3-5/6/7 đã sửa, ba negative test mới** | **682/682 xanh** (trước: 679) | `+3` test: máy thứ hai chỉ xuất hiện trên dòng lỗi value ⇒ từ chối cả file; dòng không đọc được định danh ⇒ từ chối cả file; correction `NV1 → DE1` ⇒ `ForeignKeyViolation`. `format` sạch, build **0/0** |
| 2026-09-01 | working tree audit-2 | ★★★ **N-M3-4 sửa xong — message durable sống qua recreate** | **5/5 message**, node giữ nguyên `rabbit@nvm-rabbitmq` | `make rabbitmq-durability-check`: publish 5 message durable vào quorum queue, `docker compose up --force-recreate`, đếm lại. Trước khi ghim `hostname`, cùng phép này mất sạch |
| 2026-09-01 | working tree audit-2 | ★★ **N-M3-4 — negative control, phép kiểm phải đỏ được** | node `rabbit@13565cdda431` → `rabbit@f907182d29fb`, message **không đọc được** | Gỡ tạm `hostname` khỏi compose rồi chạy lại: đỏ đúng ở vế tên node. Khôi phục, node về `rabbit@nvm-rabbitmq`. Đây là bằng chứng phép kiểm đo thật chứ không phải luôn xanh |
| 2026-09-01 | working tree audit-2 | ★ N-M3-4 — hệ quả bắt buộc: RabbitMQ chuyển nhóm B → nhóm A | `rabbitmqctl change_password` thay cho recreate | Node đã ghim nên `RABBITMQ_DEFAULT_PASS` **không còn** áp lại khi recreate — nó chỉ chạy trên node chưa khởi tạo. Không sửa `rotate-credentials.sh` theo thì lần xoay sau **thất bại im lặng** trong khi `secret-check` vẫn in `OK` |
| 2026-09-01 | working tree N-M3-1 | ★★★ **N-M3-1 — nguyên nhân thật: thiếu đường tra cứu SEGMENT, không phải thiếu index** | `Seq Scan on _hyper_4_123_chunk_compressed` **actual rows=100**, `Rows Removed by Filter: 7900`, `Buffers: shared hit=24224` | Sau migration `011`, segment exclusion **chạy đúng** — chỉ 100 segment của `FORM-01` được giải nén trên 8.000. Nhưng để tìm ra 100 đó, PostgreSQL quét tuần tự cả 8.000 dòng nén, mỗi dòng là một mảng lớn. Và không sửa được bằng index thường: predicate là `LIKE 'prefix%'`, collation `en_US.utf8` |
| 2026-09-01 | working tree N-M3-1 | ★ Migration `013` — rollup mức máy, `segmentby (site_id, machine_id, signal_code)` | **277.855 row · 10 máy · 64.217.088 → 7.405.568 byte** sau nén | CAGG phân cấp đặt trên `ts.process_signal_1m`. Một máy = một segment, nên đọc một máy là đọc một segment — phép lọc thành phép **bằng**, không còn phụ thuộc collation |
| 2026-09-01 | working tree N-M3-1 | ★★★ **D2 sau `N-M3-1` — gate XANH với biên 24,8×** | `rollup_machine_level` **p50 4,913 ms · p95 8,080 ms**, `gate=pass` | `make rollup-bench`, scenario mới đọc `ts.process_signal_machine_1m`. So với biên **3,7 %** của lần trước: nay dư **24,8 lần** |
| 2026-09-01 | working tree N-M3-1 | ★★ **Điều D2 thật sự cần: câu trả lời không còn phụ thuộc database có gì khác** | cửa sổ chỉ 1 máy **~1,2 ms** · cửa sổ có đủ 10 máy **~1,0 ms** | Đo trực tiếp, 3 lần mỗi cửa sổ. Trước `013`: **192,560** so với **1.312,404 ms** trên cùng hai cửa sổ — chênh 6,8×. Đây mới là tính chất mà con số 144 ms ban đầu **không** có |
| 2026-09-01 | working tree N-M3-1 | ★★ N-M3-1 — đối chiếu tính đúng đắn trước khi nhận con số | **10.080 và 3.488 bucket · lệch 0 `sample_count` · lệch 0 trung bình tới 9 chữ số** | Đối chiếu rollup máy với phép gộp mức kênh trên **cả hai** cửa sổ. Một câu trả lời sai thì nhanh cỡ nào cũng vô giá trị |
| 2026-09-01 | working tree N-M3-1 | ★ N-M3-1 — số block đọc của truy vấn mức máy | **33.667 → 85 block** | Nhỏ hơn **396 lần**. Đây là con số giải thích vì sao thời gian đổi hai bậc độ lớn |
| 2026-09-01 | working tree N-M3-1 | ★★ **Nợ mức line cũng đóng theo** | **1.171,168 ms → 140,831 ms** (nguội) · **19,941 / 20,303 ms** (ấm) | Cùng truy vấn line 1.000 kênh, nhưng đọc `process_signal_machine_1m`: nó gộp 10 máy thay vì 1.000 kênh. Dưới ngưỡng 200 ms kể cả lần nguội |
| 2026-09-01 | working tree N-M3-1 | D2 — ba scenario đối chứng trong cùng lượt gate | `rollup_machine` (đường cũ) **p95 151,368 ms** · `rollup_channel` **32,995 ms** · `raw_machine` **400,924 ms** | Đường cũ giữ lại **không gate**, đúng như plan §C10 đòi *"ghi cả hai bảng số"*. `raw_machine` vẫn 400,9 ms — bảng thô không được lợi gì, và đó là con số nói vì sao rollup tồn tại |
| 2026-09-01 | working tree N-M3-1 | Migration `013` — test bắt được hai chain rollback chưa biết nó | **1 test đỏ**, rồi **683/683 xanh** | `cannot drop view ts.process_signal_1m because other objects depend on it`. Một CAGG mới đặt trên rollup tạo dependant mới, và hai chain rollback liệt kê tay đều phải biết. Test làm đúng việc |
| 2026-09-01 | working tree audit-3 | ★★★ **Audit vòng 3 — ba fix ingestion CHƯA từng chạy trên runtime** | container tạo `2026-08-31T09:54:18Z`, commit fix `2026-09-01T03:57:57Z` — **cũ hơn 18 giờ** | Đúng `audit-playbook.md` §1.1, và tôi vừa mắc lại nó ngay sau khi viết ra nó. `make ci` biên dịch và test **source**; phần Docker của nó chỉ build Edge Gateway |
| 2026-09-01 | working tree audit-3 | ★★ Audit vòng 3 — fix đưa vào runtime, chứng minh bằng **hành vi** | file `live-parse-gap.csv` → `rejected/`, `row_da_luu=0` | Container tạo lại `05:36:50Z` từ image `04:15:38Z`. Thả file có máy thứ hai chỉ hiện trên dòng lỗi value vào inbox thật: bị từ chối cả file, thông báo *"names 2 machines"*, DB **0 row**. Dấu thời gian chỉ nói image mới; đây mới nói bản sửa chạy |
| 2026-09-01 | working tree audit-3 | ★★★ **Audit vòng 3 — D2 có thể xanh trên child RỖNG** | `rollup-bench.sql` nhắc `process_signal_machine_1m` đúng **1 lần** | Migration `013` tạo child `WITH NO DATA`, benchmark chỉ refresh parent, scenario child không đòi `count(*) > 0`. Live DB có 277.867 row nên **8,080 ms là số thật** — nhưng một clean deployment sẽ false-pass |
| 2026-09-01 | working tree audit-3 | ★★ D2 — assertion mới, và nó đỏ được | `child_buckets=10080 parent_buckets=10080 parent_only=0 child_only=0 sample_mismatch=0 value_mismatch=0` | Thêm refresh **parent → child** rồi đối chiếu từng bucket trước khi bấm giờ. Negative control: cùng phép trên một `machine_id` không tồn tại cho `child_buckets=0 parent_only=10080` ⇒ assertion **đỏ đúng** |
| 2026-09-01 | working tree audit-3 | ★★★ **Đường cũ hôm nay đã vượt ngưỡng — nếu nó còn là gate thì D2 đang trượt** | `rollup_machine` (đường kênh) **p95 216,024 ms** · `rollup_machine_level` **p95 12,128 ms** · `gate=pass` | Cùng một lượt chạy. Đây là xác nhận mạnh nhất rằng migration `013` cần thiết chứ không phải tối ưu cho vui |
| 2026-09-01 | working tree audit-3 | ★★ **Audit vòng 3 — đường đọc nhanh chưa dùng được bởi role production** | `scoped_child=<missing>` · `grafana_child_select=false` | Migration `013` chỉ tạo child trong schema `ts`. Một tối ưu chỉ role quyền cao chạm tới được thì chưa phải tối ưu của hệ thống — nó là tối ưu của bàn benchmark |
| 2026-09-01 | working tree audit-3 | ★ Migration `014` — child qua `ts_scoped`, K3 giữ nguyên | `rows=302732 sites=1` qua `ts_scoped`, và `permission denied for schema ts` khi đọc thẳng | Đo bằng chính role `nvm_grafana` từ container khác trên `it-net`. Không cấp quyền nào trên schema `ts` |
| 2026-09-01 | working tree audit-3 | ★★ **Audit vòng 3 — file toàn dòng lỗi vẫn vào `processed`** | 10/10 test file-drop sau khi sửa (trước: 8) | Một máy, mọi dòng lỗi value ⇒ `descriptor=null` ⇒ không rơi vào mixed-machine ⇒ ingest 0/0 ⇒ archive `return` ⇒ **move sang `processed`**. Nay vào `rejected/`. Vòng này còn để file chỉ có header vào `processed`; audit 2026-09-02 xác nhận đó là một lỗ hổng khác, không phải quyết định hợp lệ |
| 2026-09-01 | working tree audit-3 | ★★ **`rotate-verify.sh` tự nói dối khi thiếu file backup** | `PASS=18 FAIL=1` với đường dẫn không tồn tại | `old_value` trả chuỗi rỗng cho cả chín khoá; chín lần đăng nhập bằng chuỗi rỗng đều bị từ chối và được đếm là *"giá trị cũ đã chết"*. **Tám trong chín** phép đối chiếu không kiểm gì cả. Nay fail-fast, exit 2 |
| 2026-08-31 | `M3/C15` | **DỰ ĐOÁN — chủ repo, trước khi đo** · C15-1/D1b: tỉ số nén khi dung lượng ×4 ở cùng cardinality | **Gần như không đổi — lệch < 0,5 pp** so với 8,140036 % của 40 kênh × 5 ngày | Câu hỏi đặt ra trước khi bất kỳ row nào của lượt D1b được sinh. Chủ repo chọn giữa bốn khả năng: không đổi (< 0,5 pp) / tốt lên / xấu đi nhưng < 2 pp / lệch ≥ 2 pp. Đây là dự đoán đúng theo mệnh đề plan §2.4 khẳng định — nên nếu số đo bác nó thì chính lập luận của plan sai, không phải chỉ một con số lệch |
| 2026-09-01 | `M3/C15` | ★★★ **Audit docs ↔ implementation — CI hiện tại** | Build **0 warning / 0 error** · test **683/685**, **2 fail** | Hai rollback test trả PostgreSQL `2BP01`: migration `014` đã tạo scoped view phụ thuộc rollup mức máy, nhưng cả hai test bắt đầu chuỗi down từ `013`. `Down/014` tự nó hợp lệ; lỗi nằm ở rollback harness bỏ qua nó. Ghi `N-M3-9`, chưa sửa code theo yêu cầu của chủ repo |
| 2026-09-01 | `M3/C15` | ★★★ **Concurrent ingestion — deadlock không ổn định** | Full integration **46/49**; test đích cô lập **5/5 xanh** | Full suite làm `ParallelWriterDeduplicationTests` thoát PostgreSQL `40P01` tại `StoreAsync` dù code retry tối đa 5 lần. Chưa tái lập khi chạy riêng nên chưa chốt root cause; ghi `N-M3-10`, không sửa code trong audit docs |
| 2026-09-01 | working tree M3-close | ★ **N-M3-9 — rollback negative control rồi sửa đúng chain** | trước sửa **0/2 xanh**, cả hai `2BP01` · sau sửa regression **3/3 xanh** | Hai harness từng gọi `Down/013` khi scoped child từ `014` còn tồn tại. Nay gọi `Down/014`, assert view biến mất, rồi mới hạ CAGG theo thứ tự ngược; không dùng `CASCADE` để che dependency |
| 2026-09-01 | working tree M3-close | ★★★ **N-M3-8/D5 — oracle ba tầng trên stack thật** | raw/parent/child **`0/0/0 → 3/0/0 → 3/0/0 → 3/3/0 → 3/3/3`** | Run `c71b5363-2be8-4fb7-aff3-4e5a8000f352`, bucket `[10:05,10:06)Z`. Policy thật chạy thêm **1 run / 1 success / 0 failure** nhưng đúng ra vẫn bỏ sót row cũ; hai negative control `after_policy_job` và `after_parent_refresh` đều bắt `P1101` |
| 2026-09-01 | working tree M3-close | ★ **N-M3-8 — operational wide refresh phủ hierarchy** | `refreshed_seconds=60` · `refreshed_aggregates=2` · `order=parent_then_machine` | `make rollup-refresh-wide` trên đúng bucket C11, parent và child đều báo up-to-date sau reconciliation. Success chỉ in sau cả hai `CALL` |
| 2026-09-01 | working tree M3-close | ★★★ **N-M3-10 — lock graph được tái hiện trực tiếp** | **2/4** transaction thoát `40P01` | TimescaleDB `2.29.2-pg17`; server context là `ALTER TABLE _hyper... ADD CONSTRAINT ... REFERENCES ingest.processed_message`. Chunk DDL xin lock trên bảng claim sau khi writer đã claim — root cause không phải chỉ thiếu backoff |
| 2026-09-01 | working tree M3-close | ★★★ **N-M3-10 — mutation chứng minh pre-create là phần sửa** | bỏ đúng lời gọi pre-create ⇒ **119 retry, test đỏ** · khôi phục ⇒ **0 retry**, p95/max **323/323 ms** | Fixture **16 ngày mới × 8 batch/ngày = 128 row**; exact `128 claim = 128 telemetry`, replay thành 128 duplicate, đúng 16 chunk. Không tăng `MaxWriteAttempts` |
| 2026-09-01 | working tree M3-close | ★★ **N-M3-10 — stress test đích lặp trên database mới** | **5/5 xanh** | Mỗi lượt dựng container mới, chạy 16 slice chưa tồn tại với 8 batch đồng thời/slice và ép `write_retries = 0` |
| 2026-09-01 | working tree M3-close | ★★★ **Full integration sau N-M3-8/9/10** | **5 × 50/50 = 250/250 xanh** | Parallelization mặc định, chính điều kiện từng làm suite cũ lộ `40P01`; không chạy serial để né interleaving |
| 2026-09-01 | working tree M3-close | ★★★ **CI kỹ thuật tại lần định đóng M3** | format sạch · Release build **0 warning / 0 error** · test **686/686** · buffer-crash **0/200** | `make ci` exit 0. Audit 2026-09-02 mở lại milestone: kết quả này vẫn là evidence kỹ thuật, không phải bằng chứng teach-back hay quyền đóng M3 |
| 2026-09-02 | working tree audit-6 | ★★★ **N-M3-11 — empty export fail-closed** | test đích **1/1 xanh** | File chỉ có header nay vào `rejected/` cùng `.error`; telemetry **0**, archive **0**, `processed/` **0**. Đây là regression cho invariant `processed ⇒ original archived` của `ADR-033` |
| 2026-09-02 | working tree audit-6 | ★★★ **N-M3-12 — negative control verifier trước/sau sửa** | trước: `.env.example` **PASS=19 FAIL=0** · sau: exit **2** | Trước sửa verifier dùng chính placeholder như credential hợp lệ. Preflight mới dừng trước Docker và không in giá trị secret |
| 2026-09-02 | working tree audit-6 | ★★ **N-M3-12 — matrix structural preflight** | **39/39 xanh** | Happy path + thiếu backup + `.env.example` + missing/empty/duplicate/unchanged cho từng một trong chín key. Chạy độc lập, không cần `.env` thật hay stack |
| 2026-09-02 | working tree audit-6 | ★★★ **N-M3-13 — retention replay có physical oracle** | test đích **1/1 xanh** | Expired chunk đi **1 → 0** sau `drop_chunks` và giữ **0** sau replay global claim; test không còn dùng comment hay `gate=pass` hard-code thay cho phép đo |
| 2026-09-02 | working tree audit-6 | ★★★ **N-M3-14 — K3 trên scoped child** | **7/7 xanh** | Owner thấy **2** site; reader `NV1` thấy **1**, filter `DE1` trả **0**; direct raw/parent/child đều bị từ chối. Fixture refresh parent rồi child trên clean migration chain |
| 2026-09-02 | working tree audit-6 | ★★★ **N-M3-11 — probe trên runtime vừa rebuild** | telemetry/archive **102.727.256/1 → 102.727.256/1** · `rejected` **2 artefact** · `processed` **0** | `make ingestion-up` rebuild/recreate image rồi thả file chỉ có header. Runtime log EventId **2510**; file và `.error` vào `rejected/`, hai count DB không đổi. Hai artefact probe đã được xoá sau khi ghi bằng chứng |
| 2026-09-02 | working tree audit-6 | ★★★ **CI sau toàn bộ sửa P1/P2** | preflight **39/39** · format sạch · Release build **0 warning / 0 error** · test **687/687** · buffer-crash **0/200** | `make ci` exit **0**. M3 vẫn mở vì teach-back là hard DoD; CI này chỉ đóng phần kỹ thuật |
| 2026-09-02 | working tree audit-7 | ★★★ **N-M3-17 — một claimed snapshot xuyên ba pha** | regression thay public path giữa parse và archive **1/1 xanh** | Telemetry, WORM archive và `processed` cùng byte A; file B được tạo khi ingestor đang bị chặn vẫn ở inbox cho poll kế tiếp |
| 2026-09-02 | working tree audit-7 | ★★ **N-M3-22 — alert identity đúng nguyên nhân** | regression logger **1/1 xanh** · EventId **2511** có mặt · EventId 2508 không có | `.error` và alert cùng nói identity không đọc được; không còn báo giả “more than one machine” |
| 2026-09-02 | working tree audit-7 | ★★★ **N-M3-19/20 — provenance và RabbitMQ fail-fast** | preflight **45/45** · backup lịch sử ↔ burned manifest **9/9** | Matrix có fabricated/tampered backup, digest gắn sai key, manifest thiếu và node RabbitMQ thiếu; chạy thuần, chưa chạm stack |
| 2026-09-02 | working tree audit-8 | ★★★ **PAIR_RACE chạy lại trên image mang hợp đồng publish** | **7.153–7.330** lần publish trong 30 s · **7** claim rơi giữa cửa sổ · `torn_archives=0` · `inbox_leftover=none` | `make file-drop-race-probe`, container tạo `01:32:28Z`, log 2512 xác nhận hợp đồng đang chạy. Producer publish lại **cùng một tên** liên tục, xen kẽ hai nội dung. Bản marker rời cho `claimed_data=B inbox_marker=present`; bản một-file cho `claimed_data=AB inbox_leftover=none`. Mỗi export trong DB **20/20** row hoặc chưa vào — không lần nào ở giữa |
| 2026-09-02 | working tree audit-8 | ★★★ **RESERVATION_RACE chạy lại — không export nào bị khôi phục ghi đè** | `final_data=AB` · **30** lượt quan sát file trong 15 giây · cả hai tới `processed/` với **20/20** row | Cùng target. MinIO tắt ⇒ archive hỏng ⇒ claim bị trả về inbox; B publish vào đúng tên A đã đến. Bản cũ cho `final_data=A` (mất B). Lấy mẫu mỗi giây thay vì một thời điểm, vì file đang di chuyển giữa inbox và `.processing` |
| 2026-09-02 | working tree audit-8 | ★★★ **Probe tìm ra lỗi mà 679 test xanh không thấy: export MỘT DÒNG không bao giờ archive được** | *"different plant evidence"* **35** lần/lượt · **5** claim kẹt trong `.processing` | `AddTicks(1)` là **100 ns**, `curve_end_at` là `timestamptz` = **micro-giây**. Export một dòng ⇒ interval bằng 0 sau khi lưu ⇒ `ck_raw_curve_archive_interval` từ chối ⇒ quay vòng retry vô hạn; tên file retry dài thêm ~40 byte mỗi vòng cho tới khi vượt 255 byte và không move nổi. Cả hai đã sửa, mỗi lỗi một regression **đỏ được** trên bản cũ |
| 2026-09-02 | working tree audit-8 | Negative control của probe — oracle có đỏ được không | file đúng nội dung, **không** rename vào `.ready`: **0 row**, không bị claim; rename vào chỗ: **20 row** | Một probe xanh chỉ có nghĩa khi nó đỏ được. Ba lần oracle của chính probe sai trong lúc dựng (đếm archive của lượt trước, lấy mẫu đúng một thời điểm, `tr -d` nuốt newline làm hai digest dính nhau) đều cho xanh giả hoặc đỏ giả |
| 2026-09-02 | working tree audit-7 | ★★★ **Gate rút gọn theo yêu cầu owner** | format sạch · Release build **0 warning / 0 error** · test **688/688** trong **2 phút 10 giây** | Không chạy lại `buffer-crash` 200 vòng theo yêu cầu rõ ràng; bằng chứng 0/200 ở dòng audit-6 là số lịch sử, không phải số của lượt này |

---

### 2026-09-07 — Giản lược M3, giữ nguyên các gate (ADR-036/037)

HEAD `0e71ba1` + working tree của lượt sửa. Đo trên Docker Desktop đang chạy, TimescaleDB
**2.29.2 / PostgreSQL 17**, `work_mem=4MB`. `make rollup-bench` và `make rollup-bench-line`
chạy qua `nvm_grafana`/`ts_scoped`, transaction **REPEATABLE READ READ ONLY**. Một warmup rồi
mười mẫu đan xen; p95 dùng `percentile_disc`, tức mẫu lớn nhất. Query tính đủ bucket/count/mean
ở server, không tính network hoặc rendering. EXPLAIN đo riêng trên **đúng query** đó.

| Fixture / phạm vi | Nguồn | p50 (ms) | p95 (ms) | Vai trò |
|---|---|---:|---:|---|
| Riêng một máy / kênh | raw | 3,919 | 5,375 | Đối chứng |
| Riêng một máy / kênh | parent rollup | 2,087 | **2,665** | Gate < 200 ms: đạt |
| Riêng một máy / máy | machine rollup | 12,255 | **14,042** | Gate bảy ngày < 200 ms: đạt |
| Riêng một máy / máy | raw | 67,206 | 72,512 | Đối chứng |
| Riêng một máy / máy | parent rollup | 58,764 | 65,189 | Đối chứng |
| Có mười máy / kênh | raw | 3,855 | 5,290 | Đối chứng |
| Có mười máy / kênh | parent rollup | 2,113 | **2,625** | Gate < 200 ms: đạt |
| Có mười máy / FORM-01 | machine rollup | 5,482 | **11,513** | Gate trong cửa sổ có dữ liệu khác: đạt |
| Có mười máy / FORM-01 | raw | 244,942 | 262,435 | Đối chứng |
| Có mười máy / FORM-01 | parent rollup | 150,182 | 183,391 | Đối chứng |
| Cả line F1 | machine rollup | 24,541 | **29,898** | Evidence, không phải gate M3 |
| Cả line F1 | raw | 589,096 | 732,234 | Đối chứng |
| Cả line F1 | parent rollup | 472,483 | 587,990 | Đối chứng |

Fixture riêng: `[2026-07-20, 2026-07-27)` **bảy ngày**, 100 kênh / **121.429** mẫu Temperature.
Fixture line: `[2026-05-13, 2026-05-19)` **sáu ngày**, 1.000 kênh / **1.040.949** mẫu.
Cả hai đúng danh sách kênh, clock quality Good và các biên thời gian đã ghim. Không sinh thêm
fixture trong lượt này. Raw 7/6 chunk, parent và child mỗi cửa sổ 2 chunk; tất cả được metadata
công khai đánh dấu compressed. Đây không phải bằng chứng không còn rowstore tail hoặc đã
qualification hot/cold. Hai số mức máy 14,042/11,513 ms cùng bậc trong điều kiện này; không suy
rộng thành bất biến với mọi database hoặc workload đồng thời, không so tốc độ với protocol cũ.

Tám phép đối chiếu rollup ↔ raw ở hai lệnh: thiếu bucket **0**, lệch sample count **0**,
mean ngoài tolerance **0**; sai khác mean lớn nhất **4,618527782440651e-14**. Mười ba EXPLAIN
đều đọc đúng nguồn/trong cửa sổ, chunk ngoài thực thi **0**, có đối chứng ngoài cửa sổ:
raw **104/105**, mỗi rollup **4**. Identity child được kiểm bằng cùng đường với parent,
đóng `N-M3-2`; số chunk là output quan sát, không phải hằng số ép layout.

Giữ fingerprint mức đọc của D2: máy **10.080 bucket / 121.429 mẫu**, kênh **965 bucket / 1.172 mẫu**.

Sau khi giữ thêm fingerprint bucket/mẫu của gate cũ, chạy lại `make rollup-bench` trên bản cuối:
**4/4 gate đạt** (p95 kênh/máy cửa sổ riêng **4,364/21,938 ms**, cửa sổ mười máy **3,751/10,190 ms**).
Lượt này chạy cùng lúc bài buffer-crash của CI, nên giữ riêng với bảng đo phía trên;
không dùng hai lượt để khẳng định tăng/giảm tốc độ. Log: `artifacts/rollup-bench-final-2026-09-07.log`.

Sáu negative control tạo SQL tạm dưới `artifacts/`, không sửa dữ liệu persistent:

| Biến đổi có chủ đích | Kết quả |
|---|---|
| Cộng 1 vào mean của candidate rollup | exit **3**, `Per-bucket rollup equivalence failed` |
| Bỏ một bucket candidate bằng `OFFSET 1` | exit **3**, cùng lỗi equivalence |
| EXPLAIN đường máy bị chuyển từ child sang parent, đổi predicate sang equipment prefix | exit **3**, `Chunk exclusion/source proof failed` |
| Bỏ hai predicate thời gian khỏi EXPLAIN child | exit **3**, cùng lỗi chunk exclusion |
| Raw và rollup cùng mất bucket đầu tiên của fixture bảy ngày | exit **3**, `Pinned seven-day D2 bucket/sample fingerprint drifted`; equivalence vẫn xanh nên fingerprint là điều kiện độc lập |
| Gán **201 ms giả lập** cho từng mẫu timing | exit **3**, `D2 p95 must be < 200 ms` — đây là đối chứng assertion, không phải số đo hiệu năng |

Bốn đối chứng đầu giảm vòng đo xuống một warmup; lỗi equivalence dừng trước bước đo. Đối chứng SLO giữ đủ
mười mẫu. Tái lập bằng bản sao SQL runner với đúng biến đổi trên. `sh scripts/rollup-bench.sh prepare`
exit **0**: bốn refresh theo parent → child đều báo already up-to-date, không có chunk chưa nén
cần xử lý. Các target đo không còn dependency `ingestion-migrate`; preparation vẫn là lệnh có ghi.

Regression file-drop: unit mới **đỏ 1/3 trước sửa**, sau sửa **3/3 xanh**; integration
`FileDropTests` **18/18 xanh**, **1 phút 32,014 giây**. Test giữ đường không claim file chưa publish,
archive đúng snapshot, recovery và dedup. `make net-check` **9/9**, `make grafana-net-check` **3/3**.

Startup trên image ingestion mới: `PublishedSuffix` rỗng bị từ chối với `ArgumentException`
đúng tham số, exit **139**, trong container `--network none --read-only` không gắn volume dữ liệu.
Ingestion chính đã recreate lúc **2026-09-06T21:15:10.616640309Z**, image `sha256:dc7dd8da4699…`
khớp image vừa build; health **healthy**, log **2512** xác nhận `.csv.ready`, rồi `Application started`.
Không trình bày unit test source như bằng chứng container cũ đã chạy bản sửa.

`make ci` exit **0**: suite Release **700/700 xanh**, không skip, **2 phút 22,945 giây**;
rotation preflight **45/45**, format sạch, build **0 warning / 0 error**. Cùng lượt CI này,
`buffer-crash` đủ **200/200 vòng**, **0/200 lỗi**; mỗi vòng SIGKILL rồi mở lại bằng process mới.
Log: `artifacts/ci-m3-simplification-2026-09-07.log`.

Bộ SQL benchmark từ **1.668 → 246 dòng** (tính cả fixture và preparation mới), plan M3 từ
**871 → 215 dòng**. Giá trị của thay đổi nằm ở bỏ phụ thuộc catalog riêng, đóng oracle còn thiếu,
loại fallback chưa có producer cần và sửa mô tả lỗi thời; số dòng chỉ ghi quy mô diff.

Log local: `artifacts/rollup-bench-2026-09-07.log`, `artifacts/rollup-bench-line-2026-09-07.log`,
`artifacts/rollup-negative-*.sql/.log`, `artifacts/rollup-prepare-2026-09-07.log`,
`artifacts/file-drop-tests-2026-09-06.log`. Log là artefact local; bảng này giữ kết luận lâu dài.

**Giới hạn lượt sửa:** không chạy lại `compression-report`, lab retention/calendar, DST mutation,
`rollup-reconcile` hoặc file-drop race probe vì không sửa các thuật toán/policy đó và đã kiểm các
đường bị ảnh hưởng bằng targeted checks cùng full suite. Các số D1/D3/D5 và race probe trước đây
vẫn là bằng chứng lịch sử, không phải phép đo mới. Chưa chạy soak 24 giờ/hot-cold qualification
(M13), chưa thực hiện teach-back và chưa audit sâu toàn project. Plan M3/Data Collection vẫn
`in_progress`/`đang làm`; đóng ba finding này không đồng nghĩa đóng milestone.

---

## Mục tiêu SLO — còn phải đo

Khung lấy từ `docs/scope.md` Phụ lục A. Điền khi tới milestone tương ứng; mỗi ô điền xong phải
có một dòng chi tiết ở bảng phía trên.

| Chỉ số | Mục tiêu | M2 | M6 | M9 | M13 |
|---|---|---|---|---|---|
| Ingestion throughput (msg/s) | ≥ 5.000 | **3.933,2 ✗** | | | |
| Ingestion lag p95 (s) | < 5 | **99,525 ✗** | | | |
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
| Throughput bus (msg/s) | M1 publish 200 event cách nhau 100 ms — đó là kịch bản đo **mất mát**, không phải tải. N1/M2 là raw telemetry và không đi qua bus. Vẫn **chưa đo**; đo ở **M6** bằng workload outbox → consumer/projection thật |
| Chi phí SHA-1 của `IdempotencyKey` | `ADR-010` §Evidence ghi rõ đây là **phán đoán chưa đo**. Load đầu-cuối M2 không cô lập SHA-1; đo microbenchmark trước qualification N1 ở **M9** |
| Ngưỡng kill switch | Chưa bật. Harness M2 không tạo bus consumer failure traffic nên không thể chỉnh breaker. Đánh giá ở **M6** cùng workload domain-event thật (`Nvm.Bus/README.md`) |
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

---

## M4/C03 — Operator read models

Parent C02: `7d8b10c6d356abe6194cf793c6f129da8c987181`. Bằng chứng test-only được giữ ở
[M4-C03-red.patch](evidence/M4-C03-red.patch): thêm `PomNewRoutesTests` chỉ dùng HTTP và fixture test
đã tồn tại ở parent, không phụ thuộc type/table C03. Trên checkout parent riêng, áp dụng patch rồi chạy:

```sh
git apply docs/evidence/M4-C03-red.patch
dotnet build tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj --nologo
dotnet test tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj --no-build -- --filter-class '*PomNewRoutesTests' --output Detailed
```

Patch phải lấy từ commit chứa C03; không đổi branch trên working copy dirty. Ngày 2026-09-10,
trước khi thêm implementation: build 0 warning/0 error; **2/2 assertion đỏ**, cả `ProductionUnits`
và `WipBoard` trả `NotFound` thay vì `OK`. Đây là lỗi hành vi HTTP, không phải lỗi compile/missing table.
Sau implementation, **25 test mới/thay đổi đã pass qua các lượt targeted**: 2/2 route mới;
22/23 operator test ở lượt đầu hoàn chỉnh; sau khi thay metadata placeholder bằng response XML thật,
chạy riêng test metadata còn lại và **1/1 pass**. Không gọi đây là một lượt 25/25. Build 0 warning/0 error;
`dotnet format` trên các file C03 và `git diff --check` đều exit 0.

| Phép kiểm C03 | Kết quả | Phạm vi |
|---|---|---|
| Fixture PostgreSQL thật | 2.000 unit, 32 nhóm WIP, 8 equipment; seed lại thêm 0/0/0 | Testcontainers; NV1/DE1 mỗi site 1.000 unit; WIP 20/12 nhóm |
| Serial/equipment/WIP | 2.000 serial duy nhất và hợp lệ; 0 unit thiếu equipment; 0 nhóm đếm sai | So parser, catalog r3 và GROUP BY từ bảng unit; không FORM tại DE1 |
| Scope và paging | Unit NV1: 20 trang × 50; WIP test: 50+5; không lặp/thiếu/cross-site, trang cuối không nextLink | Sort có giá trị trùng nhau; WIP test thêm row trong DB cô lập rồi xoá |
| Quyền và seed lặp | SELECT có quyền, INSERT/UPDATE/DELETE không có quyền trên cả ba bảng; row đã sửa được giữ sau reseed | HTTP test dùng role runtime; admin chỉ chuẩn bị fixture |
| Metadata | XML response khớp `deploy/pom/Pom.metadata.xml`, đủ ba entity set | TestServer + PostgreSQL; Mendix import C03 chưa xác nhận |

Lệnh GREEN dùng project trên, từng class với `--filter-class '*PomNewRoutesTests'` và `--filter-class '*PomOperatorReadModelsTests'`;
test metadata riêng dùng `--filter-method '*MetadataIncludesAllThreeBoundedEntitiesWithStringKeys'`.
Log local ở `artifacts/m4-c03/tests.log`, `metadata-green.log`, `build.log`, `format.log` (không commit).
Full CI và các test C02 không thay đổi **chưa chạy lại theo yêu cầu owner**. Page p95, lab và teach-back
M4 chưa thực hiện; không dùng kết quả C03 để đóng milestone.

Runtime ngày **2026-09-11** (giờ Việt Nam), cùng working tree C03 trên parent `7d8b10c`:
`docker compose --profile execution build execution`, job `execution-prepare-poc --prepare-operator-fixture`,
rồi `up -d --wait --no-deps execution` đều exit 0. Seed thêm **2 equipment, 2.000 unit, 32 WIP rows**
trên DB C02 hiện có. Query `BEGIN READ ONLY` xác nhận ProductionUnits NV1/DE1 **1.000/1.000**,
WipBoard **20/12** nhóm và tổng UnitCount **1.000/1.000**, Equipment **5/3**.
Execution đang chạy image `sha256:7564dc267a566b4a838707e4bbfa1ce452dba91c45c90cf51cb96e2a3d4411a3`,
container tạo `2026-09-10T23:39:56.947926119Z`, UID **1654**, chỉ nối `novavolt-mes_it-net`;
`GET http://localhost:5081/health/ready` trả **200**. Không recreate Keycloak/Mendix hay chạy lại
PoC C02. HTTP có auth cho hai entity mới đã kiểm trong TestServer; **chưa kiểm qua Mendix**.

## M4/C02 — Equipment POM và PoC grid Mendix

Các phép kiểm ngày 2026-09-07/09 bên dưới được chạy trước PoC grid Mendix. Ngày 2026-09-10, owner
xác nhận phiên kiểm với agent khác đã hoàn tất C02: grid NV1/DE1 tách site, phân trang 2+1, SSO và
role-based home page hoạt động; plan và ADR-013 ghi nhận kết quả cùng `make ci` xanh. Phiên commit
`7d8b10c` dùng lại bằng chứng đó theo yêu cầu owner, không chạy lại test. Page p95 và lab M4 vẫn chưa đo.

| Ngày | Commit | Chỉ số | Giá trị | Điều kiện đo |
|---|---|---|---|---|
| 2026-09-07 | `ff158c7` + working tree C02 | Fixture Equipment lần đầu | 6 row mới: 3 NV1 + 3 DE1 | `docker compose --profile execution run --rm --no-deps execution-prepare-poc`; lấy MODULE/PACK từ catalog r3; chưa có ProductionUnits/WipBoard |
| 2026-09-07 | `ff158c7` + working tree C02 | Fixture chạy lại | 0 row mới | Cùng lệnh, cùng PostgreSQL; không sửa row/credential đã tồn tại |
| 2026-09-07 | `ff158c7` + working tree C02 | Quyền role `nvm_pom` | SELECT=true; INSERT/UPDATE/DELETE=false; CREATE schema=false | Query `has_table_privilege`/`has_schema_privilege` trên `pom.equipment`/`pom`; runtime không dùng credential migration |
| 2026-09-09 | `ff158c7` + working tree C02 | POM integration/metadata snapshot | 21/21 pass | PostgreSQL 17.9 Testcontainers + TestServer + JWT ký RSA; có invalid signature/issuer/audience/expiry, quyền/site, đổi principal, query/count/key, paging, ETag và từ chối write |
| 2026-09-09 | `ff158c7` + working tree C02 | Architecture tests | 23/23 pass | `dotnet test --project tests/Architecture/Nvm.ArchitectureTests/Nvm.ArchitectureTests.csproj` |
| 2026-09-09 | `ff158c7` + working tree C02 | Fixture chạy lại trên image cuối | 0 row mới | Image Execution `sha256:628b0a66ece767afb643ca7a41f85bf03aefd64dc0720f3dd0cf421b5b31f781`; không còn cảnh báo GSSAPI của lần chạy đầu |
| 2026-09-09 | `ff158c7` + working tree C02 | Execution, token thật NV1 | count=3; trang 2+1; filter DE1=0; key DE1=404; metadata=200; anonymous=401 | Keycloak 26.7.2, client `nvm-mendix`, audience `nvm-api`; HTTP 5081, PostgreSQL fixture thật |
| 2026-09-09 | `ff158c7` + working tree C02 | Execution, token thật DE1 | count=3; trang 2+1; filter NV1=0; key NV1=404; metadata=200; anonymous=401 | Cùng image/fixture; HTTP 5081 |
| 2026-09-09 | `ff158c7` + working tree C02 | Host.All, token thật NV1 | count=3; trang 2+1; filter DE1=0; key DE1=404; metadata=200; anonymous=401 | Process local 5080 dùng cùng POM registration/policy; dừng process sau phép kiểm |
| 2026-09-09 | `ff158c7` + working tree C02 | Host.All, token thật DE1 | count=3; trang 2+1; filter NV1=0; key NV1=404; metadata=200; anonymous=401 | Process local 5080, cùng fixture |
| 2026-09-09 | `ff158c7` + working tree C02 | Metadata thực tế | XML của Execution khớp snapshot import | So XML từ `$metadata` có Bearer thật với `deploy/pom/Equipment.metadata.xml`; chưa import vào Studio Pro |

Lệnh test POM:

```sh
dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj -- --filter-class '*PomEquipmentTests'
```

Phép kiểm HTTP thật dùng `$count=true&$orderby=Id&$top=2`, trang kế `$skip=2&$top=2`, filter
`SiteId eq '<site còn lại>'`, lookup key EOL của site còn lại và request không Authorization.
Kết quả thô local tại `artifacts/m4-c02/runtime-2026-09-09.json` chỉ có status/count/site, không token.
Execution container tạo lúc `2026-09-09T12:53:34.884172595Z`, UID 1654, chỉ nối `novavolt-mes_it-net`.

## M4/C04 — Dispatch List: kiểm UI ngày 2026-09-12

Backend HEAD `da9b08c`; Mendix HEAD `d1bb804` + working copy C04 chưa commit, Studio Pro 11.12.3.
Owner chạy F5, báo 0 errors và đăng nhập SSO vào browser Playwright riêng theo hướng dẫn dùng
`op.nv1`. Agent quan sát trực tiếp trang `Mendix - Dispatch List` tại localhost:8080; không đọc token.

| Phép kiểm UI | Kết quả quan sát |
|---|---|
| Trang sau đăng nhập, chưa lọc | Dispatch List; hiển thị 1–20/1.000 |
| Line=`P1`, Resource=`EOL-01` | Tổng 150; 20/20 dòng trang đầu có đúng Line/Resource |
| Trang đầu | 20 serial `NV1PP16250A00001`–`NV1PP16250A00020` |
| Trang kế | 20 serial `NV1PP16250A00021`–`NV1PP16250A00040`; không lặp trang đầu |
| Trang cuối | 10 serial `NV1PP16250A00141`–`NV1PP16250A00150`; 10/10 đúng Line/Resource |
| Line=`P`, Resource=`EOL-01` | 0 dòng: Line không lọc kiểu Contains |
| Line=`P1`, Resource=`EOL` | 0 dòng: Resource không lọc kiểu Contains |
| Khôi phục P1/EOL-01 | Trở về 1–20/150 |

Snapshot local ở `artifacts/m4-c04/dispatch-page1.txt`, `dispatch-page2.txt`, `dispatch-page8.txt`,
`dispatch-line-partial.txt`, `dispatch-resource-partial.txt`, `dispatch-final.txt`.
Các trang 3–7 chưa đọc hết. Request OData, phiên LineLeader/DE1 và Scan Station được kiểm bên dưới.
Page p95 chưa đo; không chạy lại C02/C03 hoặc full CI trong lượt kiểm UI này.

### Scan Station — runtime NV1 ngày 2026-09-12

Sau owner Save/F5, `mx.exe check` 11.12.3 trên app NvmShopFloor-main trả exit 0,
toàn app 0 errors. Owner đăng nhập lại phiên browser kiểm thử bằng tài khoản NV1.

| Phép kiểm | Kết quả quan sát |
|---|---|
| Menu Quét serial | Mở trang trống, nhập được Serial |
| Tra cứu `NV1PP16250A00001` bằng nút | WO-2026-0005, OPRUN-NV1-EOL-0001, P1/EOL-01, Running/Pending/AtStation |
| Enter với `NV1PP16250A00007` | Held, AtRack-HOLD, QUALITY_HOLD và lý do tiếng Việt |
| `NV1PP16250A99999`, `DE1PP16250A00001` | Cùng thông báo không tìm thấy trong site hiện tại |
| Chữ thường, 15/17 ký tự, kind X, day 000/367, sequence 00000 | Cả 7 ca báo sai định dạng |
| Day 366, sequence 99999 | Qua kiểm format, báo không tìm thấy |
| Kết quả sau 10 ca không tìm thấy/sai format | Vùng kết quả chỉ còn khoảng trắng; không giữ unit trước |
| Về danh sách → Mở dòng đầu | NV1CL16220A00001, WO-2026-0002, OPRUN-NV1-FORM-0001, F1/FORM-01 khớp dòng chọn |

Bằng chứng local: `artifacts/m4-c04/scan-validation-runtime.txt`,
`scan-open-dispatch-runtime.txt`. Quan sát request phía server được ghi riêng bên dưới;
không suy ra số request chỉ từ giao diện. Page p95 thuộc C09, chưa đo.

Phiên `op.de1` đăng nhập bằng browser automation: Dispatch mở đúng
DE1MM16230A00001/M1/MLOAD-01 và OPRUN-DE1-MLOAD-0001. Tra cứu DE1PP16250A00001
trả WO-2026-0008, OPRUN-DE1-EOL-0001, Running/Pending/AtStation.
Nhập NV1PP16250A00001 rồi Enter trả cùng thông báo không tìm thấy trong site hiện tại,
kết quả cũ rỗng. Bằng chứng: `de1-dispatch-runtime.txt`, `de1-scan-runtime.txt`.

Phép thử mất kết nối: owner dự đoán unit cũ vẫn còn. Sau tra cứu thành công NV1PP16250A00001,
agent dừng riêng `nvm-execution`, bấm Tra cứu: sau 132 ms hiện đúng thông báo lỗi kết nối,
12 trường kết quả chỉ còn khoảng trắng, input giữ nguyên. Đây là thời gian lỗi connection refused,
không phải phép đo hết timeout. Container được bật lại trong `finally`, trạng thái running/healthy;
retry trả lại đúng unit và xoá thông báo lỗi. Bằng chứng ở
`scan-connection-failure-runtime.txt` và `scan-recovery-runtime.txt` trong cùng thư mục artifacts.
`mx dump-mpr` chỉ lọc ConsumedODataService xác nhận `timeoutExpression="10"` trên bản đã lưu;
không xuất các giá trị cấu hình khác. Không có draft hay thao tác ghi nghiệp vụ trong phép thử này.

### LineLeader và request POM — runtime ngày 2026-09-12

Realm đang chạy chưa có user LineLeader. Theo yêu cầu owner, agent tạo `ll.nv1`,
`site_id=NV1`, realm role LineLeader; mật khẩu chỉ cấp trên môi trường local.
Phiên browser riêng đăng nhập tài khoản này, Home hiển thị Site NV1 và hai app role
User/LineLeader. Dispatch mở 1.000 dòng; Mở dòng đầu trả đúng NV1CL16220A00001.
Enter NV1PP16250A00007 trả Held/QUALITY_HOLD và lý do giữ; không có nút release/override.
Tra DE1PP16250A00001 báo không tìm thấy trong site hiện tại và xoá kết quả cũ.
Bằng chứng local: `artifacts/m4-c04/lineleader-runtime.txt`, `lineleader-identity.txt`.

Quan sát traffic GET `/pom/v1/` tại network namespace của `nvm-execution` bằng container
tcpdump tạm, chỉ xuất request target qua bộ lọc; không lưu packet, header hay token.
Đối chiếu với thao tác browser NV1:

| Thao tác | Request query quan sát tại server |
|---|---|
| Dispatch ban đầu | `$count=true&$top=20&$orderby=Line asc,Resource asc,Id asc` |
| Lọc Line=P1 | `$filter=Line eq 'P1'` |
| Thêm Resource=EOL-01 | `$filter=(Line eq 'P1') and (Resource eq 'EOL-01')` |
| Trang kế | Giữ filter và `$top=20`, thêm `$skip=20`; UI 20 dòng, bắt đầu NV1PP16250A00021 |
| Scan chữ thường `nv1pp16250a00001` | UI báo sai định dạng; không thấy GET POM trong cửa sổ quan sát, gồm 5 giây chờ trước đối chứng |
| Scan hợp lệ kế tiếp `NV1PP16250A00001` | `$filter=SerialNumber eq 'NV1PP16250A00001'&$top=1&$orderby=Id asc`; sau đó GET theo Id NV1-U000851 để refresh |

Capture dùng `-i any` hiển thị mỗi target hai lần; không tính đây là hai HTTP request.
Chỉ ca chữ thường được đo trực tiếp việc không gọi POM; sáu ca sai format còn lại kiểm
thông báo/clear UI và đọc nhánh validator trong model. Container quan sát tự xoá khi kết thúc,
Execution đã running/healthy sau phép thử lỗi. C04 hoàn thành kiểm chức năng và đã commit theo yêu cầu owner:
.NET `43e4b05`, Mendix `1e445d9`;
không dùng kết quả này để tuyên bố đạt p95, lab draft C09 hay toàn bộ M4.

## M4/C05 — Command store SQL: kiểm ngày 2026-09-13

Đo trên working copy từ .NET `43e4b05`, sau đó commit C05 tại `8639e75`. SQL Server test dùng container riêng
`2022-CU26-ubuntu-22.04`; test không xoá dữ liệu ứng dụng đang chạy.

| Phép kiểm | Kết quả đo | Bằng chứng local |
|---|---|---|
| Test C05 với SQL thật, DI và process host | **36/36 xanh**, 0 bỏ qua; 40,880 giây | `artifacts/c05/sql-tests-final.log` |
| Test kernel command cũ | **33/33 xanh**; 3,320 giây | `artifacts/c05/kernel-tests-final.log` |
| Test kiến trúc | **23/23 xanh**; 2,195 giây | `artifacts/c05/architecture-tests.log` |
| Toàn solution Release trong CI | **781/781 xanh**, 0 lỗi, 0 bỏ qua; build 0 warning, 0 error; format đạt | `artifacts/c05/ci-final.log` |
| Gate `make ci` hoàn chỉnh | **exit 0**; rotation preflight **45/45**; buffer crash **200/200 vòng**, **0 vòng đỏ**, mỗi vòng SIGKILL và mở lại bằng process mới | `artifacts/c05/ci-final.log` |
| Hai process khác PID, cùng submission và DB | Process đầu chạy handler, process sau replay; **1 effect, 1 outcome** | `SqlCommandProcessTests`, trong log C05 |
| Kill process sau ghi effect, trước Complete/commit | Sau kill: **0 effect, 0 outcome**; retry hoàn tất đúng một lần | `SqlCommandProcessTests`, trong log C05 |
| Oracle restart trên kernel C04 export từ `43e4b05` | **ĐỎ, exit 1**: kỳ vọng 1 effect, quan sát **2** sau hai process | `artifacts/c05/parent-red.log`; cách tái lập ở `deploy/commands/README.md` |
| Context fixture | So JSON của **2.000/2.000** row với cùng nguồn C03; seed lại thêm **0** row, không ghi đè context | `SqlCommandStoreTests`, trong log C05 |
| Runtime SQL local | **NV1: 1.000**, **DE1: 1.000** context; runtime được INSERT outcome, bị chặn DELETE outcome và UPDATE context | Query SQL sau migration/seed ngày 2026-09-13 |
| Ranh giới OT/IT sau đổi compose | **9/9 đạt** | `make net-check`, `artifacts/c05/net-check.log` |
| Execution sau build/recreate C05 | **running, healthy**, `/health/ready` trả **200** | `artifacts/c05/docker-build-final.log`, `runtime-start-final.log`; Docker inspect và HTTP local |

Image Execution cuối đã kiểm: `sha256:e4151c7fff68bcf580495f04712676a7dc3018706dc5fed5a2e59b7a4f756920`.

Test SQL còn kiểm outcome từ chối, hai scope cạnh tranh, actor/payload/result-type conflict,
natural key sai, đổi tên CLR nhưng giữ command type, rollback khi handler hoặc ghi outcome lỗi,
huỷ waiter không ảnh hưởng owner, timeout SQL cấu hình 1 giây và cách ly context theo site.
Test host chạy process Production thật cho cả Execution và Host.All khi thiếu cấu hình SQL;
test DI kiểm cả trường hợp đăng ký RAM đè sau SQL.

Giới hạn: effect trong test là bảng SQL tối thiểu, chưa phải handler nghiệp vụ C06.
Chưa đo mất ACK đúng cửa sổ SQL đã commit, chưa đo p95 hoặc tải command, chưa chứng nhận
D3–D5 qua HTTP/RabbitMQ và chưa chạy lab draft C09. Những trường hợp đó vẫn ở C06–C09.
Browser smoke bổ sung sau C05 chưa chạy được: Playwright mở `http://localhost:8080`
nhận `ERR_CONNECTION_REFUSED` vì runtime Mendix không phục vụ ở cổng đó. Kết quả browser C04
ở trên là lần đo trước, không phải phép kiểm lại trên backend C05.

## M4/C06 — Ghi nhận data collection qua Command API

Parent: `8639e75` (C05), bắt đầu ngày 2026-09-15. Chưa nghiệm thu C06.

| Phép kiểm | Kết quả đã đo | Bằng chứng local |
|---|---|---|
| RED trên source parent C05 | **1/1 đỏ đúng assertion HTTP**: kỳ vọng 200, thực tế 404; process ứng dụng đã ready, không phải lỗi build/fixture | `artifacts/c06/parent-source.tar`, `parent-build.log`, `parent-red.log`, source test tại `red-tests/` |

Parent được export bằng `git archive 8639e75` rồi build app Execution. Bộ test gọi
process đó bằng `NVM_C06_TEST_APP` và filter
`*AuthenticatedCollection_IsAcceptedOverRealHttp`; HTTP/TCP, JWT có chữ ký, SQL Server,
PostgreSQL và RabbitMQ đều chạy thật trong môi trường test riêng. Assertion đỏ chỉ chứng minh
route ghi chưa có trên parent, không dùng nó làm RED cho mọi quy tắc nghiệp vụ của C06.

## M4/C07 — runtime Mendix, lưu bản nháp và gửi lại (2026-09-23, Codex)

Working copy chưa commit; Studio Pro 11.12.3, app `C:\Users\Kingc\Mendix\NvmShopFloor-main\NvmShopFloor.mpr`.
`mx check` sau Ctrl+S/F4 của owner: 0 lỗi. Runtime localhost:8080, Execution Docker localhost:5081,
Keycloak localhost:8081; đăng nhập trình duyệt qua `/oauth/v2/login` bằng `op.nv1`.

| Phép kiểm | Kết quả quan sát |
|---|---|
| Ghi điện áp EOL 402.75 V cho `NV1PP16250A00001` | Draft `38a96885-0534-4b72-bc13-8732c996f120`; UI báo đã ghi nhận, SQL có đúng 1 row và value 402.75 |
| Reload trình duyệt rồi mở danh sách draft | Cùng submission, value và trạng thái Đã ghi nhận |
| Lưu draft 403.125 V, dừng `nvm-execution`, bấm gửi | Draft `f2ac5411-266b-49c5-bccc-2ee6580f3a63`; UI báo hệ thống không phản hồi và giữ dữ liệu |
| Start backend, reload, mở lại draft | Cùng submission và value, trạng thái Chờ xác nhận, thông báo lỗi cũ còn lưu |
| Gửi lại, sau đó bấm gửi thêm lần nữa | UI báo đã ghi nhận; SQL nhóm theo đúng submission cho 1 row, value 403.125 |
| API gửi lại cùng request sau restart backend | Outcome JSON giống nhau; correlation `885d20e8-443c-5de0-8dcf-62c8a97e4a83`, đúng 1 row SQL |
| RabbitMQ queue quorum tạm gắn exchange `nvm.production-execution` | Nhận routing key `nvm.NV1.production-execution.data-collection-recorded.v1`; ce_id `90ada25d-65bd-5e60-8268-f36d389432b0` trùng command correlation; ce_type `com.novavolt.production-execution.data-collection-recorded.v1`, ce_source `urn:novavolt:nv1:app-execution`; đã xóa queue tạm |

Snapshot browser ở `.playwright-cli/page-2026-09-23T12-27-20-113Z.yml` (saved),
`12-29-32-603Z.yml` (backend down), `12-30-11-453Z.yml` (pending sau reload),
`12-30-42-372Z.yml` (accepted sau retry), cùng tiền tố `page-2026-09-23T`.
SQL được đọc trực tiếp bằng sqlcmd trong `nvm-mssql`, không suy số row từ UI.

Giới hạn: hai lần bấm UI sau Accepted được guard phía Mendix nên không tự chứng minh backend
nhận hai request; phép replay API riêng ở trên kiểm phần backend. Chưa kiểm mất ACK sau commit,
restart runtime Mendix, role/site âm qua draft, response mapper đầy đủ hoặc p95 trang. Các gate đó còn mở.

### 2026-09-23 — C07 JSON mapping runtime (Codex)

Sau khi sửa `forceSingleOccurrence=false`, gửi lại draft `f890e540-0823-4175-910e-d3700832eb3e` hiển thị `Operation run không thuộc pack này.`; danh sách draft ghi Bị từ chối. Draft mới `3ac6b0a1-7f98-42ea-86a3-0333263fcb74`, pack `NV1PP16250A00001`, operation `OPRUN-NV1-EOL-0001`, 405.375V hiển thị thông báo đã ghi nhận và danh sách draft ghi Đã ghi nhận. Bằng chứng browser thực 21:22–21:24 + MCP check 0 lỗi. SQL execution.DataCollection: draft mới 1 row, draft rejected 0 row. Sau owner Stop/F5, browser reload và OIDC login lại: danh sách đủ 4 draft, nguyên ID/số đo/trạng thái (3 accepted, 1 rejected). Chỉ chứng minh giữ draft đã kết thúc qua restart; chưa đo Pending qua restart.

### 2026-09-23 — Production command SQL outbox integration (Codex)

Handler RecordDataCollection đã ghi event + outbox bằng IEventStore trong transaction claim/collection/outcome; endpoint không còn publish trực tiếp. Build toàn solution 0 warning/error. Lượt HTTP đầu phát hiện event đã commit nhưng worker không claim được. Phép kiểm SQL cùng bảng, cùng tài khoản `nvm_app`, trong transaction rollback: `UPDATE pending` trên CTE đầy đủ cột trả lỗi quyền UPDATE; đổi sang CTE chỉ chọn EventId rồi UPDATE bảng gốc bằng JOIN chạy được, không mở rộng quyền runtime. Bài học: test dispatcher bằng tài khoản quản trị chưa chứng minh deploy với quyền tối thiểu chạy được. Đã sửa SQL. Lượt chạy lại hoàn tất 47/47 tests, 0 failed/skipped, 106,483s (ExecutionCommandHttpTests + SqlEventOutboxTests), log `D:\Downloads\novavolt-production-outbox-tests-fixed.log`; đọc kết quả ngày 2026-09-24. Bộ HTTP dùng nvm_app: event/outbox tồn tại khi broker dừng, process bị kill trước broker phục hồi, process mới chuyển event ra broker; command replay trả cùng outcome. Inject lỗi Complete rollback cả collection/event/outbox. Đây là bằng chứng cho lát cắt này, không nghiệm thu toàn M6/N3.

### 2026-09-24 — C07 DE1 và draft Pending qua restart (Codex)

Browser đăng nhập `op.de1`: danh sách draft 0 row dù NV1 có 4 row; nhập serial/trạm NV1 bị chặn lưu với thông báo lỗi. Tạo draft DE1 `d7585761-7f2a-4645-9a0a-cfd90c9d0ccd`, serial `DE1PP16250A00001`, operation `OPRUN-DE1-EOL-0001`, trạm `NOVAVOLT/DE1/PACK/P1/EOL-01`, 406.125V. Owner Stop/F5; đăng nhập lại OIDC, danh sách còn đúng draft với trạng thái Chờ xác nhận. Mở và gửi: UI báo đã ghi nhận; SQL `execution.DataCollection` có đúng 1 row SiteId=DE1, cùng submission và value. Không có thay đổi model trong phép kiểm này; backend Docker vẫn là build trước tích hợp outbox mới. Chưa kiểm giả mạo GUID/microflow request hoặc mất response ngay sau commit trên UI.

## 2026-09-24 — Production command/outbox: forced rebuild và review

Codex: `dotnet build NovaVolt.Mes.slnx --no-restore -t:Rebuild -v quiet`: 0 warning/error (13,56s). Rebuilt DLL chạy `SqlEventOutboxTests` + `ExecutionCommandHttpTests`: **48/48, 109,354s**, log `D:\Downloads\novavolt-outbox-rebuilt-tests.log`. Publisher unit **7/7**, log `novavolt-envelope-unit-fixed.log`. Review độc lập read-only không còn finding trong hai bản sửa lease và stored CloudEvents headers; reviewer không tự chạy test.

Controlled RED: chỉ thay thuật toán dispatch bằng claim cả batch, giữ query/envelope như bản cuối; `SlowPublisher_DoesNotLeaseWaitingBatch_AndTimesOutBeforeLeaseExpires` fail vì peer claim được 0 thay vì 1. Log `novavolt-lease-red.log`. GREEN: bản cuối claim từng row + giới hạn publish bằng nửa lease, qua cùng test trong suite 48/48. Đây là phép thử trong Testcontainers; không dùng thời gian Codex disconnect làm bằng chứng runtime.

Lưu ý phép đo: restore source bằng Copy-Item giữ timestamp cũ khiến incremental build tiếp tục dùng DLL của RED. Lượt `novavolt-outbox-final-tests.log` 46/48 không chứng minh bản cuối sai; forced Rebuild ở trên mới xác nhận đúng source. Không sửa test để che lỗi. Image demo chưa được cập nhật ở thời điểm phép đo này.

### 2026-09-24 — C07 trên image outbox mới và readiness

Docker Execution được build và migrate bằng `--migrate-commands` (không reseed), container Created `2026-09-24T02:34:33.884788198Z`, image `sha256:f2e8801a87329d4db79557cc3515f1e59406e43ed0caeb0dc59bbb21af068c9f`. Browser op.de1 lưu/gửi draft `cca2f776-ba69-4a10-aa25-dd39e22e23c6`, 407.25V: SQL có một DataCollection, event `2c80ff53-0086-5e32-971c-2db8f1b8316c`, outbox Attempt=1, DispatchedAt `2026-09-24T02:37:50.6284205Z`. Queue bằng chứng bind trước submit nhận đúng submission, routing `nvm.DE1.production-execution.data-collection-recorded.v1`, ce_source `urn:novavolt:de1:app-execution`, ce_id/correlationid/causationid trùng event ID, subject/partitionkey đúng serial/site. Queue riêng đã xoá sau khi kiểm; không lấy message từ queue nghiệp vụ.

Review tiếp phát hiện readiness cũ bỏ sót quyền es/traceability. Đã thêm probe theo runtime principal cho cả hai host. Targeted HTTP `MissingRuntimeGrant_IsNotReady_AndRecoversWhenRestored`: **5/5, 24,958s** (thu hồi lần lượt es.Events INSERT, es.Streams UPDATE Version, es.Outbox UPDATE DispatchedAt, Routes SELECT, DuplicateSerialIncidents INSERT; mỗi ca 200→503→200). Log `D:\Downloads\novavolt-readiness-tests.log`. Bản sửa readiness này chưa nằm trong image demo nêu trên.

Agent projection để lại library + 4 integration test đã báo pass; parent đã đọc lại code. Chưa wire runtime. CatchUp hiện quét lại full feed để không bỏ event có GlobalSequence thấp commit muộn; cần chuyển sang inbox tăng dần trước dùng làm worker liên tục. Agent WIP để lại page Wip_Board dùng POM, refresh5s và đường mở draft; MCP check 0 errors sau khi parent tiếp quản. Timestamp/connectivity/non-overlap và runtime WIP chưa kiểm. Các agent dừng vì quota; file trên đĩa vẫn còn.

### 2026-09-24 — C07 request giả mạo và test isolation

Hai browser session thật: op.nv1 đọc danh sách chỉ thấy 4 draft NV1; op.de1 chỉ thấy 2 DE1. op.nv1 retrieve bằng GUID DE1 `30117822508078754` trả không có object. op.de1 gọi ACT_DataCollection_OpenDraft trực tiếp với GUID NV1 `30117822508040362`: callback hoàn tất nhưng page không có dữ liệu, báo Không tìm thấy bản nháp và disable Gửi.

Tạo draft Pending NV1 `144c883e-4de3-40a5-a3c9-de1cb00d0211`, GUID `30117822508078923`, 408.5V. op.de1 gọi ACT_DataCollection_Submit trực tiếp với GUID đó: owner đọc lại vẫn Pending/ReasonCode rỗng, SQL count theo SubmissionId=0. op.nv1 thử set Value=999 rồi mx.data.commit: runtime từ chối Internal server error; object trong client cache tạm là999, nhưng reload hoàn toàn rồi đọc lại trả đúng408.5/Pending. Chưa kiểm user khác cùng site (ll.nv1 dùng credential riêng không có trong realm export); không thay mật khẩu người dùng. Draft NV1 trên đang được giữ Pending để tiếp tục kiểm quyền.

Full CI đầu 877/879 (3m45,138s), hai lỗi SqlEventOutboxTests. Lỗi append-only test để lại row trong queue dùng chung giữa các test, nên thứ tự chạy làm test slow publisher nhận thêm event; đây khác controlled RED cũ. Sửa mỗi test dùng database riêng trên cùng container, không xoá event store để reset queue và không tăng timeout/nới assertion. Targeted **5/5,21,927s**, log `novavolt-outbox-isolation-tests.log`; full CI đang chạy lại. `make net-check` **9/9**, log `novavolt-network-check.log`.

### 2026-09-24 — Projection inbox, worker và runtime thật

Codex trực tiếp: inbox/consumer targeted6/6 (67,069s), test SQL-source→consumer riêng1/1 (35,941s). Thêm worker/role test: lần đầu lỗi do fixture lấy ConnectionString từ NpgsqlDataSource đã mở và bị lược password; sửa fixture lấy credential gốc Testcontainers, không nới quyền. Targeted worker restart + quyền cập nhật duy nhất cột applied + revoke INSERT làm readiness fail: **1/1,8,226s**. Runtime role không sửa/xoá fact trong inbox.

Full suite trước worker: **880/881,4m00,717s**, một lỗi Append1000 p95 **23,608ms**, max184,686ms; buffer-crash đang chạy đồng thời. Không kết luận nguyên nhân từ tương quan. Sau lab, cùng test không đổi chạy riêng **1/1,24,529s** (dưới20ms; runner không in output test xanh nên không ghi p95 cụ thể). Full suite có worker đang chạy lại, giữ ngưỡng nguyên vẹn.

`make buffer-crash`: **200/200 vòng,0 đỏ**; mỗi vòng SIGKILL rồi reopen bằng process mới, không mất record đã xác nhận. Log novavolt-buffer-crash-final.log. Dockerfile Simulator/Ingestion/Gateway/LoadHarness đã thêm COPY .editorconfig để Docker dùng cùng quy tắc analyzer với local; không hạ severity để chữa build.

Demo projection Created2026-09-24T03:23:57.259578681Z, imagef96949e6a5536a144959cab7afa3b7c0807c4a3f5434d393e4ac4a40031d9d80. Execution Created2026-09-24T06:41:21.590554272Z, image05d37504fc0fca5974b810a0847504fdfe44d41ea6cbce5c91032288f9f5a3c7 (đã có readiness mới). Hai container healthy. Explicit migration tạo role nvm_projection riêng trên mỗi DB; runtime không có admin credential.

API có token op.nv1 (client nvm-mendix có audience nvm-api) cấp serial **NV1CL16267A70485**, start STACK, record3.6V, complete. Dừng nvm-projection sau start; gửi measurement/complete và replay cả4 request khi worker đang dừng; start lại. SQL:4event,maxversion4; outbox4/4dispatched. PostgreSQL: đúng1unit Completed/version4; inbox4/4applied. Event IDs lần lượt48a81115-183c-5de6-8703-8bac5f74ee2b,ea15f614-4409-5070-9b25-0322c7c65e46,9362521f-0367-5b7a-9254-e58901f14d39,8bfdeb3b-fe48-58f3-8fcd-2631ac3e3cde. Log novavolt-projection-smoke.log. Chưa nối projection vào POM, không dùng kết quả này đóng M6.

Reconciliation explicite sau smoke chạy thành công, unit vẫn version4/count1. Rotation preflight45/45, secret-check đạt, staged diff không chứa giá trị secret từ .env. Full suite có worker tiếp tục trượt p95 **52,486ms**, max1187,265ms dù crash lab đã kết thúc: dữ liệu bác việc coi riêng lab crash là nguyên nhân. Đặt EventStoreTests vào xUnit collection DisableParallelization để phép đo latency không tranh chấp với các collection khởi tạo SQL/Postgres/DDL; số event và ngưỡng20ms giữ nguyên. Đây là thay đổi điều kiện đo, không phải tuyên bố tối ưu runtime; cần full suite mới xác minh.

Suite worker kết thúc881/882,4m28,009s, chỉ lỗi p95 trên. Bản nonparallel collection đang build/test lại; log novavolt-isolated-latency-suite.log. Chưa commit/push đợt code này trước khi gate sạch.

### 2026-09-24 — Gate sau khi tách phép đo latency

Full solution **882/882,0skip,4m02,165s**; integration3m59,553s. Log novavolt-isolated-latency-suite.log. Collection hiệu năng chạy không song song với các collection khác; assertion1000event/p95<20ms không đổi. Điều này xác nhận gate ở điều kiện đo mới, không chứng minh SLO dưới tải dây chuyền hay nguồn gốc duy nhất của mọi lần fail trước.

Sau gate: thêm source-generated serializer +5 golden v1 (fixture mới, không đổi golden cũ); targeted contract5/5,2,320s. Bổ sung metadata bất biến cho event Traceability mới; lifecycle integration3/3,23,838s kiểm subject/correlation/causation/partition trong envelope SQL. Event cũ giữ nguyên metadata thiếu optional attribute; chưa redeploy image cho thay đổi metadata này. Ownership mâu thuẫn giữa bảng event và scope M5 được chốt bằng ADR-042, không đổi tên v1 đã lưu.

Final format thành công; Release build0warning/error8,98s; toàn bộ contract21/21,1,907s (novavolt-contract-final.log).

Delivery: commitd15e50e8e9d68361039eddb78a89e33dc344c360 đã pushmain; fetch ban đầu DNSfail, pushHTTP mặc định bị aborted và ls-remote xác nhận vẫn da9b08c; retryHTTP/1.1 thành công da9b08c→d15e50e. Không forcepush.
Execution metadata image216b338c0b4823f2bac5baaf140c1a415b12862560f519b7de3c523a6ff13c7d, Created2026-09-24T07:01:30.967990231Z,healthy. Smoke serialNV1CL16267A77279:4command+4replay,worker stop/start,SQL4event cósubject urn:trace-unit:cell:NV1CL16267A77279 vàpartitionNV1:NV1CL16267A77279;PGunitCompleted/version4,inbox4/4applied. Lognovavolt-projection-metadata-smoke.log.

### 2026-09-24 — Live POM và context command authoritative (Codex, chưa phải nghiệm thu milestone)

- PostgreSQL query views cutover từ fixture sang rm.unit_current + duplicate holds; không xoá fixture. ETag dùng read revision tăng theo transaction, không dùng max global sequence làm revision khi event tới muộn. ProjectedPomTests1/1; ProjectionEngineTests7/7; PomOperatorReadModelsTests23/23 (20.314s).
- ADR-043: query Contracts do Traceability hosting replay; transaction giữ reservation/stream tới commit. SqlEventStore dùng scoped transaction cho read-own-writes và site filter. Traceability4/4 (32.963s), EventStore6/6 (38.091s), LiveProductionContextHttp2/2 (36.084s), architecture24/24 (3.917s).
- HTTP regression lần đầu46/48: hai kiểm restart nhận0event trong5s. Code có lease120s, nhưng chưa có snapshot lease của hai lần đó; không khẳng định đã chứng minh nguyên nhân. Test durability sau crash chờ tối đa150s và ghi snapshot outbox, vẫn đòi đúng1event. Lượt sau48/48 (2m11.883s); cả hai snapshot lượt này đã dispatched trước assertion, nên đây là regression xanh chứ không tái lập failure ban đầu.
- Runtime Execution image e8c9acf45939280f385432bd40ca98b4f639250699d688b058b16905ff7133f2, container created2026-09-24T08:57:16Z, healthy. API+Keycloak tạo NV1PP16267A95137 / DE1PP16267A80388, startEOL và record409.625V, retry cùng identity. SQL mỗi serial1measurement; lifecycle mỗi serial2event/2outbox; PostgreSQLRunning/version2; POM trả run41ký tự và workorder dài hơn32 mà không cắt chuỗi. Đây chưa phải Mendix E2E.
- Mendix user Update POM_v1metadata; mx check bắt6CE6621 vì imported attributes chưa tự đổi length. Agent sửa6length trong NvmShared qua MCP, document0errors và whole-app0errors. F5 sau reboot thất bại: RemotePrimaryKeyAnalyzer không hỗ trợ WipBoard._Id48→128. Khôi phục key48 ở DTO/file/model; chờ user Update consumed metadata/F5, chưa có runtime recovery proof.

- Full suite novavolt-live-full-suite.log:807/891pass,84fail,4m11.487s. Failure SQL container startup701/exit118 trong các collection chạy đồng thời. Đã giới hạn xUnit MaxThreads2; full rerun chưa xác nhận. Không dùng targeted pass thay thế full gate.

- Sửa tương thích khóa WIP: ProjectedPomTests1/1,51.680s, HTTP metadata Id MaxLength48 và step20 không cắt. Mendix user Update+SaveAll, MCP/mx check0errors; đang chờ F5. Execution imagefa7c6c10b236 / Projection03b92c3ce9b4 healthy, created2026-09-24T14:08:01Z; HTTP thật metadata48, NV1unit3/WIPsum3, DE1unit1/WIPsum1. Full bounded suite đang chạy, chưa có kết luận.
- Runtime recovery: user xác nhận F5 chạy được sau Update key48; agent HTTP8080 trả200. Format verify hai file sửa key/test đạt. WIP browser đang kiểm, full bounded suite chưa kết thúc.

- Full bounded suite: **891/891,0skip,8m48.157s**, integration8m47.244s (novavolt-live-bounded-suite.log). MaxThreads2 tránh startup hàng loạt trên rig; giữ toàn bộ assertions và race bên trong test. Đạt ngưỡng suite<10phút ở lượt này, không thay các DoD tải/soak.
- Mendix live unit: op.nv1 Dispatch hiển thị3unit; mở NV1PP16267A95137 thấy đúng workorder dài và run41ký tự. Draft1fd08f97-c589-443c-b22d-b34fab7e89f6 ghi410.125V từ form; UI xác nhận đã ghi nhận, SQL đúng1row với cùng submission/run. WIP page chưa được gán quyền mở; đã thêm menu, chờ user gán page Navigation → Visible for=User/SaveAll/F5 (tên field xác nhận từ ảnh Studio Pro11.12.3).

- Browser DE1 sau recovery: op.de1 Dispatch chỉ1row DE1PP16267A80388, Running/EOL/P1, workorder dài đầy đủ; NV1 phiên riêng3row. Commitc13a6d92f348a7a531af71be00961680dbbc9302 đã pushmain, ls-remote khớp. Full pre-commit format đạt. Mendix model chưa commit; WIP đang chờ page access UI.

### 2026-09-26 — WIP hai site và refresh với phản hồi chậm

Codex; backend code `c13a6d92f348a7a531af71be00961680dbbc9302`, docs HEAD `e128c69`; Mendix11.12.3 main `1e445d9` + model dirty của nhiệm vụ. User gán Visible for và chạy F5 sau restart. Export security xác nhận Wip_Board cho Operator/LineLeader. Không có thay đổi backend trong phép đo.

Lab tái lập ở `mendix/labs` (Playwright-core1.63.0, Chrome local), đối chiếu từng cột với HTTP POM đã xác thực và kiểm fixture không đổi đầu/cuối:

| Lượt | Nhóm browser = POM | Request hoàn tất | Thời gian mỗi request (ms) | Max đồng thời |
|---|---|---|---|---|
| NV1 baseline | L1/STACK/Pending/2; P1/EOL/Pending/1 | 3 | 47,737; 48,866; 43,961 | 1 |
| DE1 baseline | P1/EOL/Pending/1 | 3 | 31,345; 30,630; 40,705 | 1 |
| NV1 giữ phản hồi7,5s | Hai nhóm như trên | 2 | 7574,675; 7565,855 | 1 |

Cả hai lệnh `npm run wip`, `npm run wip:slow` exit0. Mọi request HTTP200, không lỗi transport, không còn request dở dang. Slow start/end lần lượt4917,161→12491,836ms và17514,389→25080,244ms từ mốc quan sát; lượt sau bắt đầu khoảng5s sau khi lượt trước kết thúc. Đây là điều phối phía browser trên fixture nhỏ, **không** phải SLO backend hay phép kiểm tải lớn. C08 last-success/connectivity chưa triển khai.

Lỗi callback500 không tái hiện trong hai lệnh lab xanh trên, nhưng tái hiện với op.de1 trong lượt probe SDK tiếp theo. Probe NV1 trước đó xác nhận getData.get(entity=NvmShared.WipBoard,filter offset/limit/sort) trả đúng hai nhóm của site, qua cùng implementation mà SDK mx-api/data.retrieveByEntity gọi. Probe DE1 chưa tới bước retrieve. Log StudioPro xác nhận app trước đã đóng rồi mở lại; m2ee_log chỉ chứa startup. User cung cấp Console: LicenseRuntimeException “Maximum number of sessions exceeded! (You are currently using a trial license)” tại SessionManager.createSession → OIDC.SetSessionData. Đây xác nhận nguyên nhân callback500 mới nhất. Lab cũ chỉ context.close nên không gọi logout; đang thêm cleanup có xác nhận HTTP logout. Chưa lấy được số phiên trước/sau nên không định lượng số phiên do từng lượt lab để lại.

- Sau user Stop/F5 để giải phóng session cũ, lab có logout chạy lại thành công: baseline NV1/DE1 mỗi bên3request, slowNV1 hai request7554,052/7557,124ms, maxConcurrent1, mọiHTTP200, exit0. Logout phải trảHTTP200 trước context.close, cả khi assertion lỗi. Không coi phép này là đo số session còn lại trên server.
- Widget C08 riêng: `mendix/widgets/wip-board`, SDK `mx-api/data` external; build MPK bằng tool11.12.0, npm mendix11.8 theo dependency tool.6tests scheduler/failure/recovery/pagination/dispose qua. Root sửa scaffold còn thiếu widgetName/package.xml và Rollup config, dedupe dependency để resolve React typings; build cuối exit0. Package SHA256 `50AC03C3ADF83CCD84CD13233359E9DAF3D805C3BCF86F3C9DE7B4AFB20FFD67`. Đã copy vào app/widgets và chờ userF4; chưa có model/runtime proof cho widget mới.

### 2026-09-26 — Widget WIP mới trên runtime11.12.3

UserF4 nạp MPK; root thay Data Grid2 bằng widget và giữ nút mở draft qua MCP. Page check0errors, whole mxcheck0errors. Build log app_bundle hoàn tất04:04:09Z; deployment page có mã widget. Đã xem ảnh Connected/Disconnected,4cột và snapshot hiển thị đầy đủ.

- `npm run wip`: NV1/DE1 mỗi3request SDK `retrieve_by_xpath`, đúng4cột so POM và không đổi fixture đầu/cuối. NV1durations46,751/47,372/50,481ms; DE1durations36,937/33,545/41,348ms. MaxConcurrent1, HTTP200, unfinished0, exit0; timestamp sau poll khác ban đầu.
- `npm run wip:slow -- --capture`: NV1 hai request7540,971/7542,834ms; max1,HTTP200,unfinished0,exit0. Timestamp chỉ từ thành công cùng request.
- `npm run wip:failure`: abort riêng SDK request trong browser; Disconnected, giữ nguyên4cột/timestamp, bỏ chặn và Retry → Connected/time mới, exit0.
- `npm run wip:backend-down -- --capture`: dừng đúng nvm-execution; WIPDisconnected và giữ2nhóm/time cũ, start/healthready rồiRetry → Connected/time mới, exit0. Sau lab dockerinspect xác nhận runninghealthy; không sửa DB/seed/model trong lab.
- Mỗi context đã gọi logout có xác nhận HTTP200 trước đóng, không tiếp tục lỗi trialsession trong các lượt này. Ảnh QA local tại output/playwright/wip (gitignored). Không dùng các lượt nhỏ này kết luận p95<1,5s hoặc nghiệm thu M4 toàn bộ.

## M5 — Lab ranh giới aggregate, 96 cell đồng thời (2026-09-26, Claude)

Commit `2fffdf2` + test `AggregateBoundaryLabTests` (chạy với `NVM_RUN_LABS=1`). SQL Server 2022 Testcontainers, Docker Desktop 16 CPU / 8 GB. 480 command/biến thể, mỗi command replay stream rồi append qua claim idempotency thật. Một lần chạy.

| Biến thể | Xung đột | p50 ms | p95 ms | p99 ms | Wall s | Event replay |
|---|---:|---:|---:|---:|---:|---:|
| Một aggregate pack, đọc có khoá | 0 | 427,7 | 6.977,8 | 8.759,9 | 11,07 | 114.960 |
| Một aggregate pack, optimistic + retry | 27.520 | 449,1 | 108.397,9 | 143.737,2 | 149,84 | 4.951.982 |
| Mỗi cell một stream + link riêng | 0 | 37,5 | 1.302,8 | 1.732,5 | 2,00 | 960 |

Kết luận ở [ADR-044](adr/ADR-044-mot-aggregate-cho-moi-unit.md).

## M6 — Genealogy 100k cell (2026-09-26, Claude)

`GenealogyScaleLabTests` (NVM_RUN_LABS=1), SQL Server 2022 + PostgreSQL 17 Testcontainers. 100.000 cell / 8.000 module / 1.000 pack, 504.200 event, 504.000 cạnh, closure 636.407 dòng (117 MB). Seed 184,6 s; rebuild 65,8 s. 200 mẫu mỗi truy vấn: forward closure p95 2,28 ms (CTE 211,17 ms); backward closure p95 1,62 ms (CTE 3,91 ms); span cuộn p95 2,47 ms. Kết luận ở [ADR-006](adr/ADR-006-closure-table-cho-forward-trace.md).

## M7 — Saga formation/aging (2026-09-26, Claude)

`FormationAgingProcessTests`: 12,04 ngày ảo trong 563–582 ms. `FormationAgingScaleLabTests` (NVM_RUN_LABS=1), 30.000 cell Aging: poll không có hạn p95 0,94 ms; rack/level 100 dòng 67 ms; xả 30.000 timeout tuần tự 54/s (operator p95 28 ms), song song 8 là 301/s trong 99,6 s (operator p95 39,3 ms). Kết luận ở [ADR-015](adr/ADR-015-saga-formation-aging-timeout-ben-trong-sql.md).

## M8 — Matching 100k cell (2026-09-26, Claude)

`MatchingTests` (NVM_RUN_LABS=1), 16 CPU, dữ liệu tổng hợp `MatchingDataset` (97.371 cell trong bin). greedy 1.546 module 0,65 s; greedy-ocv 4.776 module 0,35 s; cp-sat 7.498 module (7,59 % tồn dư) 21,17 s. Property test 10.000 ca: 0 vi phạm. Lab bỏ MaxDistinctLots (20.000 cell): lot tối đa/module 3 → 8, module/lot TB 98,4 → 102,8. Kết luận ở [ADR-016](adr/ADR-016-cp-sat-cho-matching.md).

## M9 — Hold cascade 3.000 pack (2026-09-26, Claude)

`QualityCascadeScaleLabTests` (NVM_RUN_LABS=1), SQL Server 2022 + PostgreSQL 17 Testcontainers, 16 CPU, một lần chạy mỗi phiên bản. 315.000 unit (3.000 pack), 315 chunk × 1.000. Phiên bản đọc lại stream mỗi chunk: 63,92 s (trượt N9). Phiên bản giữ version trong `CascadeJobs`: **35,35 s** (8.911 unit/s); chạy lại 0 chunk. Seed 306,8 s, ngoài phép đo. **Chưa đo** phần "ingestion không giảm quá 10 %" của N9: cần N1 đã nghiệm thu, rig còn trượt preflight. Kết luận ở [ADR-017](adr/ADR-017-hold-cascade-la-job-co-checkpoint.md).

## M10 — OEE ground truth và cô lập site (2026-09-26, Claude)

Không phải benchmark hiệu năng. `EquipmentOeeTests`: hai line, OEE gộp 0,8003 = 25.930/32.400, trung bình cộng 0,8582. `CrossSiteIsolationHttpTests` + lab bỏ filter site: phát hiện ở aging/due và 7 test POM. Chi tiết ở [ADR-045](adr/ADR-045-recipe-material-equipment-m10.md).
