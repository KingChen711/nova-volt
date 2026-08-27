# Từ vựng

Từ điển nghiệp vụ của dự án. Mỗi mục nói từ đó nghĩa gì **trong nhà máy NovaVolt**, không phải nghĩa chung trên Wikipedia.

## Luật ghi

1. **Chưa có trong file này thì không được dùng.** Agent muốn dùng một thuật ngữ nghiệp vụ trong báo cáo, ADR, plan hay comment code mà nó chưa có ở đây → thêm vào **trong chính commit đó** ([`AGENTS.md`](../AGENTS.md) §5.8.2).
2. **Một từ, một mục.** Giải thích cùng một từ ở ba chỗ thì ba chỗ đó sẽ dần lệch nhau.
3. **Ngắn.** 1–3 câu. Giải thích dài thuộc về plan hoặc ADR; ở đây chỉ cần đủ để đọc tiếp mà không phải dừng lại tra.
4. **Chỗ dễ nhầm phải nói ra.** `EOL` có hai nghĩa; "state" có ba loại. Đó là phần giá trị nhất của file này.
5. Có nguồn thì trỏ về `scope.md` §, đừng chép lại.

---

## 1. Sản phẩm và cấu tạo

| Từ | Nghĩa |
|---|---|
| **Cell** | Viên pin đơn — đơn vị nhỏ nhất được cấp serial. Ký tự `C` trong serial |
| **Module** | Nhiều cell + khung + busbar. Ký tự `M` |
| **Pack** | Sản phẩm giao cho hãng xe, chứa module (sản phẩm A) hoặc chứa thẳng cell (sản phẩm B). Ký tự `P` |
| **Cell-to-Pack (CTP)** | Thiết kế **bỏ tầng module**, cell lắp thẳng vào pack. Sản phẩm B `NV-C100-LFP-CTP` dùng kiểu này — đây là lý do routing phải là **dữ liệu**, không phải `switch-case` |
| **NMC / LFP** | Hai hệ hoá học cathode. NMC (Nickel-Mangan-Cobalt) năng lượng cao; LFP (Lithium-Iron-Phosphate) rẻ và bền hơn |
| **Electrode** | Điện cực — lá kim loại được phủ vật liệu hoạt tính. Anode và cathode |
| **Separator** | Màng ngăn giữa anode và cathode |
| **Electrolyte** | Dung dịch điện giải, bơm vào ở bước `FILL` |
| **Busbar** | Thanh dẫn nối các cell trong module |
| **BMS** | *Battery Management System* — bo mạch điều khiển pack. Nạp firmware ở bước `BMSFLASH` |

## 2. Công đoạn sản xuất

Routing đầy đủ ở [`scope.md`](scope.md) §2.2.

| Từ | Nghĩa |
|---|---|
| **Routing** | Chuỗi bước công đoạn của một sản phẩm. Là **dữ liệu trong DB**, không phải code — sản phẩm B bỏ qua step 300–330 |
| **Step code** | Mã một bước: `MIX`, `COAT`, `STACK`, `FORM`… |
| **`MIX`** | Trộn bùn điện cực (slurry). Đơn vị trace là **lot** |
| **`COAT`** | Phủ bùn lên lá kim loại thành cuộn dài. Truy vết tới **vị trí mét trên cuộn** |
| **`SLIT`** | Xẻ cuộn mẹ thành cuộn con, kèm **ánh xạ toạ độ** để giữ được vị trí mét gốc |
| **`STACK`** | ★ Xếp chồng điện cực thành hình viên pin. **Serial ra đời ở đây** — ranh giới giữa truy vết theo lot và truy vết theo cá thể |
| **`FILL` / `SEAL`** | Bơm điện giải và hàn kín |
| **Formation** (`FORM`) | **Lần nạp điện đầu tiên** của cell vừa lắp. Tạo lớp **SEI** quyết định tuổi thọ pin. Kéo dài hàng giờ, cell nằm trong **tray**, cắm vào một **channel** |
| **SEI** | *Solid Electrolyte Interphase* — lớp màng hình thành trên anode trong lúc formation |
| **Tray / Channel** | Khay giữ cell và kênh sạc riêng của nó trong máy formation. Một máy có thể có 1.000 kênh, ví dụ `FORM-01-CH-0142` |
| **`DEGAS`** | Xả khí sinh ra trong formation |
| **Aging** (`AGE`) | Cell **nằm nghỉ** vài ngày tới vài tuần. Không phải chờ vô ích — cell lỗi sẽ **tự xả điện** và lộ ra |
| **`EOL`** | ⚠️ **Hai nghĩa.** Step 440 = *End-of-**Line*** (test cuối chuyền). Cuối vòng đời pin = *End-of-**Life***. Trong code dùng `ProductionEol` và `LifecycleEol`, **không bao giờ** `EOL` trần |

## 3. Đo lường và chất lượng

| Từ | Nghĩa |
|---|---|
| **OCV** | *Open Circuit Voltage* — điện áp khi không tải. `OCV2` là lần đo thứ hai, sau aging. **Sụt nhiều = self-discharge = cell lỗi** |
| **ACIR** | *AC Internal Resistance* — điện trở trong. Cao bất thường = tiếp xúc kém hoặc vật liệu lỗi |
| **Self-discharge** | Cell tự mất điện khi nằm yên. Dấu hiệu của lỗi bên trong |
| **Grading** (`GRADE`) | Từ OCV + ACIR + dung lượng → gán cell vào một **bin**. Cell không "đạt/trượt" mà **được phân hạng** |
| **Bin** | Nhóm chất lượng sau grading |
| **Matching** (`MATCH`) | Chọn cell **cùng hạng** ghép thành module. Lý do vật lý: **cell yếu nhất giới hạn cả pack** |
| **Quarantine / Hold** | Đơn vị bị **giữ lại** vì nghi ngờ. Chưa phải loại bỏ — chờ MRB phán quyết |
| **Hold cascade** | Phát hiện lô vật liệu lỗi → **lan** lệnh giữ xuống mọi cell/module/pack đã dùng nó. Có thể tới 3.000 pack |
| **Containment** | Khoanh vùng ảnh hưởng của một sự cố chất lượng |
| **NCR** | *Non-Conformance Report* — biên bản không phù hợp, mở khi phát hiện sai lệch |
| **MRB** | *Material Review Board* — hội đồng quyết định số phận hàng bị giữ: dùng được / rework / scrap |
| **Disposition** | Phán quyết của MRB |
| **Rework** | Tháo ra sửa rồi đo lại |
| **Scrap** | Loại bỏ vĩnh viễn |
| **E-signature** | Chữ ký điện tử có định danh người ký. Bắt buộc cho release khỏi quarantine và duyệt recipe |
| **Yield** | Tỉ lệ sản phẩm đạt trên tổng sản xuất |

## 4. Truy vết (traceability)

| Từ | Nghĩa |
|---|---|
| **Lot** | Một *mẻ* hoặc một *cuộn* — đơn vị truy vết **trước** khi có serial. Ví dụ `ROL-NV1-260825-CT1-004` |
| **Serial / Serial number** | Mã 16 ký tự khắc laser lên từng đơn vị, ví dụ `NV1CL16238A00123`. Layout ở [`scope.md`](scope.md) §6.1 |
| **Trace unit** | Thứ mà truy vết theo dõi: `cell` / `module` / `pack` / `lot`. Xuất hiện trong `urn:trace-unit:cell:…` |
| **Genealogy** | Quan hệ cha–con giữa các đơn vị: cuộn → cell → module → pack. Là **DAG có thời gian**, không phải cây ([`scope.md`](scope.md) §6.4) |
| **Span** | **Vị trí mét trên cuộn** mà một cell được cắt ra, ví dụ `[1250.00, 1250.82]`. Máy phủ thường lỗi theo đoạn chứ không lỗi cả cuộn — truy được tới mét thì recall vài chục cell thay vì vài nghìn |
| **Web side** | Mặt A hay mặt B của lá điện cực |
| **Forward trace** | *"Cuộn/lot này đi vào những pack nào?"* — câu hỏi lúc **recall** |
| **Backward trace** | *"Pack này làm từ những gì?"* — câu hỏi lúc **điều tra một lỗi** |
| **Recall** | Triệu hồi sản phẩm đã giao |
| **DPP** | *Digital Battery Passport* — hồ sơ số bắt buộc khi xuất pin sang EU |
| **EPCIS** | Chuẩn GS1 mô tả sự kiện chuỗi cung ứng. Dự án mượn ngữ nghĩa cạnh genealogy từ đây |
| **GS1 Digital Link** | Chuẩn GS1 để mã hoá định danh sản phẩm thành URL |

## 5. Vận hành và tổ chức

| Từ | Nghĩa |
|---|---|
| **Work Order (WO)** | **Lệnh sản xuất** từ ERP: *"làm 5.000 cell mã NV-P120-NMC, giao 12/9"*. Kéo dài nhiều ngày, nhiều ca. Ví dụ `WO-2026-0042` |
| **Operation Run (OPRUN)** | **Một lần chạy** của **một bước** trên **một máy**. Ví dụ `OPRUN-8891`. Hẹp hơn work order rất nhiều |
| **Recipe** | Bộ tham số công nghệ của một bước trên một máy, có **version**. Auditor sẽ hỏi *"lúc 14:20 ngày 12/3 máy này chạy tham số nào?"* |
| **Effectivity** | Khoảng thời gian một version recipe có hiệu lực |
| **Shelf life** | Hạn dùng của một lot vật liệu |
| **Exposure time** | Thời gian vật liệu được phép ở ngoài môi trường kiểm soát trước khi phải bỏ |
| **WIP** | *Work In Progress* — hàng đang dở trên chuyền |
| **OEE** | *Overall Equipment Effectiveness* — chỉ số hiệu suất thiết bị |
| **Ca (shift)** | 3 ca/ngày: `A` 06–14, `B` 14–22, `C` 22–06 |
| **Production day** | Ngày dương lịch mà **ca A của chu kỳ đó bắt đầu**. Ca C từ 22:00 ngày 25 tới 06:00 ngày 26 vẫn thuộc `production_day = 25` |
| **Site** | Một nhà máy. `NV1` Hải Phòng (không DST), `DE1` Leipzig (**có DST**, cố ý) |
| **Multiplant** | Một hệ thống chạy nhiều site cùng lúc, dữ liệu không được rò rỉ chéo |
| **Retention** | Giữ dữ liệu bao lâu rồi mới được xoá. Ở đây: event store và genealogy **15 năm** (thực chất là không xoá), telemetry thô **400 ngày**, rollup 1 phút 15 năm |
| **Legal hold** | Cờ chặn **mọi** retention policy. Đang có tranh chấp pháp lý thì không được xoá gì, kể cả dữ liệu đã quá hạn |
| **Audit** | Đợt kiểm tra của bên thứ ba hoặc của khách hàng, đối chiếu hồ sơ với thực tế. Câu hỏi điển hình: *"lúc 14:20 ngày 12/3, máy này chạy tham số nào?"* |
| **IATF 16949 / ISO 9001** | Hai chứng nhận hệ thống chất lượng NovaVolt đang giữ. Mất chứng nhận = mất khách hàng ô tô |
| **Hồ sơ pháp lý** | Dữ liệu mà luật hoặc chứng nhận buộc phải giữ nguyên vẹn. Khác log ứng dụng ở chỗ: **không được sửa, không được xoá**, sai thì ghi bút toán bù trừ (`AGENTS.md` K4, K5) |

## 6. ISA-95 và cây nhà máy

| Từ | Nghĩa |
|---|---|
| **ISA-95** | Chuẩn phân cấp hệ thống sản xuất. Dự án dùng 6 bậc: `Enterprise → Site → Area → Line → WorkCell → Equipment` |
| **Area** | Khu công đoạn: `ELECTRODE`, `ASSEMBLY`, `FORMATION`, `AGING`, `MODULE`, `PACK`, `WAREHOUSE` |
| **Line** | Chuyền trong một area: `L1`, `L2` (cell), `M1` (module), `P1` (pack) |
| **WorkCell** | Trạm/máy trong một chuyền, ví dụ `FORM-01` |
| **Equipment path** | Chuỗi đầy đủ `NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142`. Dùng làm MQTT topic, nhãn metric, khoá phân quyền, XPath trong Mendix — **quyết định một lần, dùng khắp nơi** |
| **Revision** *(của factory model)* | Một phiên bản của **tài liệu** mô tả cây nhà máy. Thêm kênh sạc, tháo work cell, đổi tên line → revision mới, **không sửa tại chỗ**. Cần thiết vì hồ sơ traceability năm ngoái trỏ tới equipment path có thể **không còn tồn tại** hôm nay — và đó không phải dữ liệu hỏng |
| **Activation** *(của revision)* | Đưa một revision **vào hiệu lực tại một nhà máy**. Tài liệu nằm trên đĩa cả tuần cũng chưa có tác dụng gì cho tới khi được kích hoạt |
| **Staged rollout** | Triển khai từng nhà máy một. NV1 chạy revision 12 trong khi DE1 còn ở 11 — bình thường và cố ý, không phải lệch pha cần sửa |
| **MES** | *Manufacturing Execution System* — hệ thống điều hành sản xuất, đứng giữa ERP và tầng thiết bị |
| **OT / IT** | *Operational Technology* (tầng thiết bị nhà máy) và *Information Technology* (tầng doanh nghiệp). Ranh giới giữa hai tầng là ranh giới **an ninh** |
| **DMZ** | Vùng đệm giữa OT và IT |
| **ERP** | Hệ thống quản trị doanh nghiệp. Đẩy work order xuống MES, nhận kết quả lên |
| **B2MML** | Định dạng XML trao đổi dữ liệu ERP ↔ MES theo ISA-95 |

## 7. Ba loại state — không được nhập làm một enum

Đây là chỗ nhiều hệ thống MES tự viết làm sai ([`scope.md`](scope.md) §6.2).

| Loại | Ai sở hữu | Ví dụ |
|---|---|---|
| **Execution state** | Production Execution | `Scheduled`, `Running`, `Completed`, `Aborted` — của **thao tác**, không phải của unit |
| **Quality state** | Quality | `Pending`, `Released`, `Held`, `Rework`, `Scrapped` — **độc lập với vị trí vật lý** |
| **Inventory / location state** | Material & WIP | `InTransit`, `AtRack-A12-L3`, `Shipped` |

> Một khay cell nằm trong kho thành phẩm **vẫn có thể đang bị giữ**. Gộp ba loại này thành một enum thì một thao tác chuyển kho sẽ vô tình release hàng lỗi — và đó là loại lỗi bị audit phát hiện.

## 8. Kiến trúc và messaging

| Từ | Nghĩa |
|---|---|
| **Bounded context** | Vùng mà mỗi từ có **đúng một nghĩa**. Ranh giới là **ngôn ngữ**, không phải kỹ thuật — xem §7 ở trên để thấy vì sao |
| **Functional Block (FB)** | Đơn vị đóng gói của Opcenter EF: một bounded context có entity, command, handler, schema DB riêng |
| **CloudEvents** | Chuẩn CNCF quy định **tên và ý nghĩa** các trường bao ngoài một sự kiện (`id`, `type`, `source`, `time`, `subject`…) |
| **Envelope** | Lớp bao ngoài sự kiện. Bên trong (`data`) là nội dung nghiệp vụ; bên ngoài là siêu dữ liệu để định tuyến, dedup, audit. **Hai loại, đừng nhập một**: envelope của *framework* (MassTransit tự bọc để định tuyến/retry — thay thư viện là mất) và envelope *nghiệp vụ* (CloudEvents §7.4 — sống trong event store, không được đổi). Xem `ADR-008` |
| **Transport header** | Siêu dữ liệu đi kèm message ở tầng giao thức, **ngoài** body. Đọc được kể cả khi body không deserialize nổi — đó là lý do thuộc tính CloudEvents nằm ở đây |
| **`source`** *(CloudEvents)* | *"Ai nói điều này"* — `urn:novavolt:{site}:{app}`. Thứ đầu tiên người ta nhìn khi hai service bất đồng về cùng một đơn vị |
| **`subject`** *(CloudEvents)* | Sự kiện **nói về cái gì** — ví dụ `urn:trace-unit:cell:NV1CL16238A00123` |
| **`dataschema`** *(CloudEvents)* | URL tới định nghĩa schema của `data`, để bên thứ ba xác thực payload |
| **Exchange** | "Bưu cục" của RabbitMQ. Producer gửi **vào exchange**, không gửi thẳng vào queue |
| **Routing key** | Địa chỉ ghi trên message: `nvm.NV1.traceability.unit-serialized.v1`. **Phân biệt hoa thường** |
| **Binding** | Quy tắc consumer đăng ký, ví dụ `nvm.NV1.#`. Producer **không biết** ai đang nghe — đó là "bus-centric" |
| **Queue** | Hộp thư của một consumer. 2 consumer + 2 queue = cả hai cùng nhận (fan-out); 2 consumer + 1 queue = tranh nhau |
| **Dead letter** / `_error` | Nơi message rơi vào sau khi thử lại đủ số lần vẫn hỏng. **Không mất**, chỉ đứng riêng |
| `_skipped` | Message tới đúng queue nhưng **không consumer nào nhận kiểu đó**. Đầy lên = binding sai hoặc consumer chưa deploy |
| **Retry** | Chạy lại consumer khi nó ném exception. Ở đây retry nằm **trong cùng một lần delivery** — message không quay lại broker, nên consumer bị chiếm suốt cả chuỗi |
| **Jitter** | Cộng nhiễu ngẫu nhiên vào khoảng cách retry. Không có nó, mọi consumer hỏng vì **một nguyên nhân chung** sẽ thử lại đồng pha, và đợt sóng retry đổ về đúng lúc hệ thống yếu nhất |
| **Kill switch** / circuit breaker | Tạm dừng một endpoint khi tỉ lệ lỗi vượt ngưỡng. Giữ message **nằm trong queue** thay vì đốt hết ngân sách retry rồi rơi vào `_error`. Chưa bật — xem `Nvm.Bus/README.md` |
| **Scheduled redelivery** | Trả message về broker để thử lại sau **hàng phút/giờ**, khác retry trong-delivery. Cần plugin RabbitMQ mà image không có |
| **Correlation id** | Nhóm mọi event thuộc cùng một luồng nghiệp vụ. Ở đây thường là **work order** |
| **Causation id** | Cái **trực tiếp gây ra** event này, thường là operation run hoặc command. *Correlation nhóm lại, causation xâu chuỗi* |
| **Partition key** | Nhóm message phải **giữ đúng thứ tự** với nhau. Hai event cùng một cell không được vượt mặt nhau; hai cell khác nhau thì được |
| **Command** | Một **ý định** thay đổi trạng thái, gửi tới **đúng một** handler, đặt tên ở thể mệnh lệnh (`QuarantineUnit`). Đối lập với event: event là **sự thật đã xảy ra**, thì quá khứ, ai nghe cũng được và có thể không ai nghe |
| **Cross-cutting concern** | Mối quan tâm cắt ngang **mọi** thao tác — kiểm hợp lệ, chống trùng, ghi vết, transaction. Viết một lần ở pipeline thay vì nhớ viết lại trong từng handler |
| **Pipeline behavior** | Một tầng bọc quanh command handler. Chạy theo thứ tự đăng ký, tầng đầu nằm ngoài cùng, mỗi tầng tự quyết có gọi tiếp hay không |
| **Audit trail** | Sổ ghi **thay đổi trạng thái của sản phẩm**, auditor đọc, IATF 16949 đòi phải còn. **Khác log**: log để debug hôm nay, được phép lấy mẫu, xoay vòng, tắt đi |
| **Natural key** | Bộ trường **vốn có trong dữ liệu** đủ để nhận ra một sự việc, không cần ID do hệ thống cấp. Với một phép đo: `(site, equipment, unit, step, device_timestamp, signal)`. Là đầu vào để suy ra khoá dedup — xem `ADR-010` |
| **Idempotency** | Xử lý cùng một message hai lần cho kết quả như xử lý một lần |
| **At-least-once** | Message **sẽ** đến nhiều hơn một lần. Đây là mặc định của thế giới thật, không phải sự cố |
| **Dedup** | Nhận ra và bỏ qua bản trùng |
| **Event versioning** | Mỗi event mang số version ngay từ v1. Thêm field optional → **không** tăng version; đổi ý nghĩa / xoá field / đổi kiểu → **tăng**, và viết upcaster |
| **Upcaster** | Hàm chuyển event **v1 → v2**. Đọc event năm 2026 bằng code năm 2036 bằng cách chạy chuỗi upcaster |
| **Golden file** | File JSON **thật** của một version, giữ nguyên đời đời. Test đọc nó và assert code hôm nay vẫn hiểu. **Không bao giờ sửa file cũ** |
| **Outbox** | Bảng trung gian để ghi DB và publish message trong **cùng một transaction**. Lời giải cho bẫy dual-write |
| **Dual-write** | Ghi hai hệ thống không cùng transaction. Process chết giữa chừng → mất dữ liệu hoặc mất message |
| **Saga / Process manager** | Luồng nghiệp vụ dài ngày có trạng thái, ví dụ chờ đủ ngày aging |
| **Sparkplug B** | Chuẩn payload MQTT cho thiết bị công nghiệp, có `NBIRTH`/`NDEATH`/`bdSeq`/`seq` |
| **Store-and-forward** | Edge gateway đệm dữ liệu xuống đĩa khi backend chết, gửi bù khi backend sống lại |

## 9. Ba loại timestamp — đừng bao giờ trộn

[`scope.md`](scope.md) §7.3.

| Trường | Nguồn | Đáng tin? | Dùng cho |
|---|---|---|---|
| `device_timestamp` | Đồng hồ PLC | **Hay sai**, lệch được hàng giờ | Thứ tự sự kiện thật trên máy |
| `gateway_timestamp` | Lúc edge gateway nhận | Khá hơn, có NTP | Đo độ trễ mạng OT |
| `recorded_at` | Lúc hệ thống ghi nhận | Đáng tin nhất | **Audit**, retention, replay |

**Lưu cả ba, không bao giờ ghi đè.** `clock_quality` (`Good` / `Drifted` / `Unknown`) tính từ độ lệch giữa hai cái đầu.
