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
| **Model catalog** | Toàn bộ các revision của cây nhà máy **đã publish**, giữ cùng lúc. Khác với *revision đang có hiệu lực*: catalog là cả kệ sách, activation là quyển đang mở ở một nhà máy. Cần cả kệ vì event báo cây đổi phải **so hai tài liệu** — cái đang chạy và cái sắp chạy (`ADR-024`) |
| **Staged rollout** | Triển khai từng nhà máy một. NV1 chạy revision 12 trong khi DE1 còn ở 11 — bình thường và cố ý, không phải lệch pha cần sửa |
| **MES** | *Manufacturing Execution System* — hệ thống điều hành sản xuất, đứng giữa ERP và tầng thiết bị |
| **OT / IT** | *Operational Technology* (tầng thiết bị nhà máy) và *Information Technology* (tầng doanh nghiệp). Ranh giới giữa hai tầng là ranh giới **an ninh** |
| **DMZ** | Vùng đệm giữa OT và IT |
| **Zones & conduits** | Mô hình an ninh của **IEC 62443**: chia nhà máy thành **zone** (vùng cùng mức rủi ro) và chỉ cho đi lại qua **conduit** (ống dẫn được khai báo, kiểm soát). `ot-net` / `dmz-net` / `it-net` là ba zone; EMQX là conduit duy nhất giữa OT và DMZ |
| **Jump host** | Máy trung gian đặt ở DMZ mà kỹ sư phải đăng nhập vào **trước**, thay vì nối thẳng laptop xuống tầng thiết bị. Trong repo: `make dmz-shell`. Nó chỉ có một chân ở DMZ — máy trạm của con người **không** được đứng hai chân sang OT |
| **Unified Namespace (UNS)** | Một cây topic MQTT duy nhất mà mọi hệ thống đọc/ghi, thay cho hàng chục tích hợp điểm-điểm. Cây đó chính là **equipment path** ở trên — cùng chuỗi dùng làm topic, nhãn metric và khoá phân quyền |
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
| **Fan-out** | Một message tới **mọi** consumer đang quan tâm, mỗi consumer một queue riêng. Publisher không biết có bao nhiêu bên nghe, và thêm bên thứ ba không đụng gì tới publisher |
| **Competing consumer** | Nhiều consumer **chung một queue** để chia tải: mỗi message chỉ một trong số đó nhận. Hợp lệ, nhưng nhầm nó thành fan-out thì audit trail mất một nửa số dòng mà không có lỗi ở đâu |
| **Quorum queue** | Loại queue bền của RabbitMQ 4, thay cho classic mirrored queue đã bị gỡ. Trên một node dev hai loại chạy như nhau — chọn sai chỉ lộ ra khi có node thứ hai, và lúc đó đổi loại nghĩa là **xoá queue** cùng những gì còn trong nó |
| **Dead letter** / `_error` | Nơi message rơi vào sau khi thử lại đủ số lần vẫn hỏng. **Không mất**, chỉ đứng riêng |
| `_skipped` | Message tới đúng queue nhưng **không consumer nào nhận kiểu đó**. Đầy lên = binding sai hoặc consumer chưa deploy |
| **Retry** | Chạy lại consumer khi nó ném exception. Ở đây retry nằm **trong cùng một lần delivery** — message không quay lại broker, nên consumer bị chiếm suốt cả chuỗi |
| **Jitter** | Cộng nhiễu ngẫu nhiên vào khoảng cách retry. Không có nó, mọi consumer hỏng vì **một nguyên nhân chung** sẽ thử lại đồng pha, và đợt sóng retry đổ về đúng lúc hệ thống yếu nhất |
| **Health check** / probe | Một câu hỏi hệ thống tự đặt cho chính nó và trả lời được bằng máy: *"SQL Server có với tới được không?"*. Mỗi probe có tên riêng vì tên đó là thứ hiện lên dashboard lúc 3 giờ sáng |
| **Liveness** (`/health/live`) | *"Process còn sống, đừng restart tôi."* **Chỉ** kiểm trong process. Cho một dependency chết vào đây nghĩa là orchestrator sẽ giết một service khoẻ mạnh mỗi lần database chậm |
| **Readiness** (`/health/ready`) | *"Dependency của tôi với tới được, gửi traffic sang."* Đỏ thì instance bị rút khỏi **rotation** (danh sách nơi load balancer gửi request tới) và tự quay lại khi dependency sống lại. Đây là nơi dependency chết được phép làm đỏ |
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
| **Claim** *(chỗ giữ)* | Chỗ giữ cho một khoá dedup **trong lúc** lệnh đang chạy. Ba trạng thái, không phải hai: *chưa thấy* · *đang bay* · *đã xong*. Không có trạng thái giữa thì hai bản của cùng một lệnh tới cùng lúc đều thấy "chưa ai làm" và đều chạy. Xem `ADR-023` |
| **At-least-once** | Message **sẽ** đến nhiều hơn một lần. Đây là mặc định của thế giới thật, không phải sự cố |
| **Dedup** | Nhận ra và bỏ qua bản trùng |
| **Event versioning** | Mỗi event mang số version ngay từ v1. Thêm field optional → **không** tăng version; đổi ý nghĩa / xoá field / đổi kiểu → **tăng**, và viết upcaster |
| **Upcaster** | Hàm chuyển event **v1 → v2**. Đọc event năm 2026 bằng code năm 2036 bằng cách chạy chuỗi upcaster |
| **Analyzer** *(Roslyn)* | Đoạn code chạy **bên trong compiler** lúc build, soi cây cú pháp và báo lỗi theo quy ước riêng của dự án. Khác linter ở chỗ nó **chặn build**, không chỉ nhắc |
| **Diagnostic** | Một phát hiện của analyzer, có id (`NVM001`), thông báo và mức độ. Mức độ khai ở `.editorconfig`, không phải trong code analyzer |
| **Architecture test** | Test duyệt **metadata của assembly đã build** để kiểm ranh giới giữa các thành phần. Bắt được thứ analyzer không bắt: `#pragma warning disable` bịt được analyzer, nhưng không xoá được dependency khỏi metadata |
| **Đối chứng dương** *(positive control)* | Một test áp đúng phép kiểm của rule lên thứ **phải** bị bắt. Không có nó, một rule nhìn nhầm chỗ sẽ báo "không có vi phạm" và xanh vĩnh viễn |
| **Wire contract** | Type có payload đi **trên dây** hoặc vào event store — nên hình dạng của nó bị ràng buộc bởi thứ đọc nó 10 năm sau, không phải bởi thứ viết nó hôm nay. Ở đây: mọi thứ trong `Nvm.Contracts` cộng mọi `IDomainEvent`. Xem `NVM002` |
| **Golden file** | File JSON **thật** của một version, giữ nguyên đời đời. Test đọc nó và assert code hôm nay vẫn hiểu. **Không bao giờ sửa file cũ** |
| **Outbox** | Bảng trung gian để ghi DB và publish message trong **cùng một transaction**. Lời giải cho bẫy dual-write |
| **Dual-write** | Ghi hai hệ thống không cùng transaction. Process chết giữa chừng → mất dữ liệu hoặc mất message |
| **Saga / Process manager** | Luồng nghiệp vụ dài ngày có trạng thái, ví dụ chờ đủ ngày aging |
| **Sparkplug B** | Chuẩn payload MQTT cho thiết bị công nghiệp, có `NBIRTH`/`NDEATH`/`bdSeq`/`seq` |
| **Protobuf** | Định dạng nhị phân mà Sparkplug B dùng cho payload. Mỗi trường đi trên dây bằng **số thứ tự (field number)**, không bằng tên — nên đổi một số trong file `.proto` là đổi ý nghĩa của byte, và bên đọc **không** báo lỗi: số lạ được đọc thành *unknown field* rồi bỏ qua. Đây là lý do bản `.proto` trong repo bị pin bằng SHA-256 (`ADR-026`) |
| **Metric** *(Sparkplug)* | Một tín hiệu đo được của thiết bị: `Formation/Voltage`, `Formation/Temperature`… Có tên, kiểu, giá trị và dấu thời gian riêng — **dấu thời gian của từng metric**, không chỉ của cả message, vì một message gom nhiều lần đo ở các thời điểm khác nhau |
| **Alias** *(Sparkplug)* | Số thay cho tên metric. Thiết bị khai `name ↔ alias` **một lần** ở birth, rồi mọi message sau chỉ gửi số. Tiết kiệm băng thông tầng OT, và trả giá bằng một ràng buộc cứng: **mất birth là mất luôn khả năng đọc mọi message sau đó** — không có cách nào suy ra tên từ một con số. Đó là việc của **rebirth** |
| **`DBIRTH` / `DDATA`** | Cặp message của một **thiết bị** (device), song song với `NBIRTH`/`NDATA` của **edge node** đứng trên nó. `DBIRTH` khai toàn bộ metric kèm tên, alias và kiểu; `DDATA` chỉ mang alias và giá trị đã đổi. Một `DDATA` đứng một mình là **không đọc được** — đúng theo thiết kế, không phải thiếu sót |
| **Store-and-forward** | Edge gateway đệm dữ liệu xuống đĩa khi backend chết, gửi bù khi backend sống lại |
| **Report-by-exception** *(RBE)* | Thiết bị chỉ gửi khi giá trị **đổi**, không gửi theo nhịp cố định. Đường truyền tầng OT hẹp và máy thì nhiều, nên đây là mặc định của Sparkplug — cùng lý do nó dùng **alias** thay tên metric. Hệ quả phải nhớ: *"không nhận được gì"* có thể là **máy vẫn ổn, giá trị không đổi**, và cũng có thể là **mất kết nối**. Phân biệt hai thứ đó là việc của `NDEATH`, không phải của timeout |
| **Rebirth** | Yêu cầu edge node **khai báo lại** toàn bộ metric bằng một `NBIRTH` mới. Cần khi consumer phát hiện `seq` nhảy cóc — tức đã bỏ lỡ ít nhất một message — hoặc gặp alias chưa từng thấy. Không có rebirth thì consumer chỉ còn hai lựa chọn đều tệ: đoán bừa, hoặc mù cho tới lần node khởi động lại |
| **`bdSeq`** *(birth/death sequence)* | Số phiên của một edge node, tăng mỗi lần node nối lại. Nằm trong cả `NBIRTH` lẫn `NDEATH` (qua MQTT Last Will), nên khớp được cặp birth–death **của đúng một phiên**. Nếu chỉ nhìn `NDEATH` mà không nhìn `bdSeq`, một `NDEATH` đến muộn của phiên cũ sẽ giết nhầm phiên mới vừa lên |
| **Backpressure** | Bên nhận **nói ra** rằng nó đang quá tải, và bên gửi **chậm lại** — thay vì bên gửi cứ đẩy cho tới khi bên nhận sập. Ở đây: ingestion trả `429`/`503`, gateway giảm tốc flush. Ngược với **retry storm**: cả khu mất mạng cùng lúc thì cũng nối lại cùng lúc, và nếu mỗi gateway phản ứng với lỗi bằng cách thử **nhanh hơn**, một sự cố mạng 30 giây thành sự cố hệ thống nửa tiếng |
| **Retry storm** | Vòng lặp tự siết: bên nhận quá tải → trả lỗi → bên gửi retry dày hơn → bên nhận quá tải nặng hơn. Thứ chặn nó là backpressure cộng exponential backoff **có jitter** — jitter để n client không cùng thử lại ở đúng một mốc |
| **Poison message** | Message mà consumer **không bao giờ** xử lý nổi, dù thử bao nhiêu lần. Retry không cứu được; nó chỉ làm nghẽn endpoint. Chỗ đúng của nó là `_error` queue (bus) hoặc thư mục `rejected/` (file drop), kèm lý do đọc được |

## 9. Ba loại timestamp — đừng bao giờ trộn

[`scope.md`](scope.md) §7.3.

| Trường | Nguồn | Đáng tin? | Dùng cho |
|---|---|---|---|
| `device_timestamp` | Đồng hồ PLC | **Hay sai**, lệch được hàng giờ | Thứ tự sự kiện thật trên máy |
| `gateway_timestamp` | Lúc edge gateway nhận | Khá hơn, có NTP | Đo độ trễ mạng OT |
| `recorded_at` | Lúc hệ thống ghi nhận | Đáng tin nhất | **Audit**, retention, replay |

**Lưu cả ba, không bao giờ ghi đè.** `clock_quality` (`Good` / `Drifted` / `Unknown`) tính từ độ lệch giữa hai cái đầu.
