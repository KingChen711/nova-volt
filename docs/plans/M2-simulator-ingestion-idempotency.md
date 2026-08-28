---
title: "M2 — Simulator, Ingestion & Idempotency"
milestone: M2
duration: "2,5 tuần (24 giờ 45 phút ước lượng)"
status: planned
created: 2026-08-28
depends_on: [M0, M1]
unlocks: [M3]
---

# M2 — Simulator, Ingestion & Idempotency

> **Mục tiêu**: nuốt được dữ liệu thiết bị **đúng**, kể cả khi thiết bị gửi bậy — và chứng minh bằng số, không bằng niềm tin.
>
> Đọc trước: [`AGENTS.md`](../../AGENTS.md) §4 (K1, K2, K3, K7, K11, K12) · [`scope.md`](../scope.md) §7.1 (Sparkplug B), §7.2 (idempotency), §7.3 (ba loại timestamp), §9/M2, §13.1 (ranh giới network) · [`M1-factory-model-bus.md`](M1-factory-model-bus.md) §8

---

## 1. Definition of Done

Milestone chỉ được đóng khi **cả 5** mệnh đề đúng, có bằng chứng chạy được:

| # | Tiêu chí | Cách chứng minh |
|---|---|---|
| ★ D1 | Chạy **1 giờ** với **10% duplicate**: số bản ghi trong DB **khớp chính xác** số phép đo logic — không dư, không thiếu | Reconciliation test: simulator đếm số phép đo **logic** đã sinh, query DB đếm số row, hai số bằng nhau. N4, T1 |
| D2 | **≥ 5.000 msg/s** duy trì **10 phút**, p95 lag device→DB **< 5 s** | Load harness chạy trong `ot-net` · metric `ingest.lag` · số vào `benchmarks.md` (N1, N2) |
| D3 | Tắt toàn bộ backend **2 phút** → simulator **vẫn chạy bình thường**; bật lại → **0 message mất trên chặng thiết bị → ingestion**, backlog tiêu hết **< 3 phút** | Lab §5.C10.2. Mệnh đề này **đã được phát biểu lại** so với `scope.md` — lý do ở §2.3 (N15 đạt ở M2; N3 toàn hệ thống vẫn ở M13) |
| D4 | `NDEATH` làm **mọi** metric của node chuyển `STALE`, **không** xoá dữ liệu lịch sử | Test: publish `NDEATH` → query trạng thái node = `STALE`, `SELECT count(*)` trên telemetry **không đổi** |
| D5 | Message có `device_timestamp` lệch **2 giờ** vẫn được nhận, gắn cờ `Drifted` | Simulator bật fault lệch đồng hồ → row có `clock_quality = 'Drifted'` và vẫn có mặt |

**Sản phẩm phụ bắt buộc**

- `make ci` vẫn xanh, số test tăng thật (M1 kết thúc ở **328**).
- **4** ADR mới: `ADR-026` (thư viện Sparkplug), `ADR-027` (chặng gateway → ingestion), `ADR-028` (store-and-forward trên đĩa), `ADR-029` (backpressure và rate limit khi flush). Mỗi ADR viết **trong chính commit ra quyết định**.
- `ADR-010` nhận thêm §Evidence: **con số bản ghi thừa khi bỏ dedup** (lab §5.C13.3). `scope.md` §9/M2 đã đòi đúng điều này.
- `docs/event-catalog.md`: các event M2 thật sự phát ra chuyển sang `Đã cài đặt = có`.
- [x] `docs/glossary.md` đã bổ sung **backpressure**, **retry storm**, **rebirth**, **report-by-exception**, **`bdSeq`**, **poison message** — làm **trước** khi code, đúng `AGENTS.md` §5.8.2 (2026-08-28).
- `Nvm.Simulator` và `Nvm.EdgeGateway` có Dockerfile và chạy **trong container ở đúng network của mình** (`scope.md` §13.1 — bắt buộc, không phải khuyến nghị).
- `make net-check` vẫn **9/9** sau khi thêm ba service mới.

**Không** thuộc M2: event store, outbox, projection, `ProductionUnit`, genealogy, TimescaleDB hypertable tuning, OData. M2 dựng **đường vào** của dữ liệu, không dựng nghiệp vụ trên dữ liệu đó.

> [!important] Ranh giới dễ trôi nhất của M2
> M2 dễ phình thành M3 và M5 cùng lúc. Dấu hiệu: bắt đầu viết continuous aggregate, bắt đầu tính yield, bắt đầu tạo `ProductionUnit` từ một `DDATA`. Dừng lại.
>
> Câu hỏi kiểm tra: *thứ tôi đang viết có phải là "đưa dữ liệu vào đúng và đủ" không?* Nếu nó **diễn giải** dữ liệu — tính toán, tổng hợp, ra quyết định nghiệp vụ — thì nó thuộc milestone sau. M2 chỉ chịu trách nhiệm: **cái vào bằng cái ra, không dư không thiếu**.

---

## 2. Phát hiện trước khi bắt đầu — bốn điều chỉnh so với `scope.md`

Cả bốn đã kiểm bằng lệnh hoặc bằng file thật trong repo, không phải suy đoán.

### 2.1 `dmz-net` **không** tới được RabbitMQ — gateway không publish thẳng lên bus được

| | |
|---|---|
| **Phát hiện** | [`scope.md`](../scope.md) §9/M2 nói `Nvm.EdgeGateway` *"buffer trên đĩa khi backend down"* mà không nói **backend** là ai và gateway nói chuyện với nó bằng gì. |
| **Bằng chứng** | [`scripts/net-check.sh:127`](../../scripts/net-check.sh) — `check "DMZ -> rabbitmq:5672 (tang IT)" $DMZ rabbitmq 5672 blocked`. Đây là một trong 9 phép đo đang xanh, tức ranh giới này **đang được ép**, không phải sơ suất. |
| **Hệ quả** | Gateway đứng ở `dmz-net` **không thể** `Publish` lên bus. Bảng network ở `scope.md` §13.1 xếp `Nvm.Ingestion` ở **`dmz-net` + `it-net`** — nên Ingestion mới là điểm bắc cầu, và chặng **gateway → ingestion** phải là một giao thức chạy trong `dmz-net`. `scope.md` không nói giao thức đó là gì. |
| **Xử lý** | Chốt ở §3/Q1, ghi `ADR-027`. **Không** kéo RabbitMQ xuống `dmz-net` — làm vậy là biến vùng đệm thành cửa sau vào tầng IT, đúng thứ K11 cấm. |

> Đây không phải lỗi của scope. Scope viết bảng network ở §13.1 và mô tả M2 ở §9 cách nhau vài trăm dòng, và chỗ nối giữa hai phần chưa ai đi qua. M2 là lần đầu tiên có người đi.

### 2.2 Chưa có package MQTT hay protobuf nào — và license phải kiểm trước khi pin

`Directory.Packages.props` hiện **không có** dòng nào cho MQTTnet, SparkplugNet hay `Google.Protobuf`. M2 là commit đầu tiên thêm chúng.

M1/C09 đã để lại một bài học đắt: MassTransit v9 chuyển sang license thương mại, và nếu không kiểm trước thì pin xong mới biết. `MassTransitPinTests` tồn tại vì chuyện đó. **Áp dụng cùng quy trình cho C01**: đọc license của bản định pin, ghi vào `ADR-026`, và ép bằng test chứ không bằng comment.

### 2.3 `scope.md` nói *"0 message mất"* ở M2, nhưng N3 được gán cho M13 — D3 phải phát biểu lại

| | |
|---|---|
| **Phát hiện** | DoD của M2 trong `scope.md` §9 viết *"bật lại → **0 message mất**"*, tham chiếu N3 và N15. Nhưng bảng NFR ở [`scope.md`](../scope.md) §4 gán **N3 → M13**. |
| **Vấn đề** | Ở M2 **vẫn chưa có transactional outbox** — nó là nội dung của M6. Chặng `Ingestion → RabbitMQ` do đó vẫn là dual-write, đúng tình huống `ADR-022` mô tả và M1/C13.3 đã **đo được**: **18/200 event mất** khi broker chết 30 giây. Đòi *"0 message mất"* cho **toàn** đường đi ở M2 là đòi một mệnh đề chưa thể đạt. |
| **Đề xuất** | Tách đường đi thành hai chặng và chỉ đòi 0 ở chặng M2 thật sự bảo vệ được: <br>· **thiết bị → ingestion (DB)**: **0 mất** — store-and-forward trên đĩa của gateway chịu trách nhiệm. Đây là D3. <br>· **ingestion → bus → consumer**: **chưa** 0 mất, và không đóng được ở M2. Giữ ở M6 cùng outbox, đo lại bằng đúng kịch bản của M1/C13.3. |
| **Ảnh hưởng** | M2 giữ nguyên thời lượng. **N15** (*MES down không làm dừng dây chuyền*) vẫn đạt ở M2 — simulator chạy tiếp khi backend tắt là đúng mệnh đề đó. **N3** giữ ở M13. `scope.md` §9/M2 cần sửa câu chữ — làm ở C19 cùng lượt với checklist. |

> Cùng dạng với §3.3 của M1: không hạ tiêu chuẩn, mà **nói đúng tiêu chuẩn nào thuộc về milestone nào**. Một DoD không thể đạt thì hoặc bị bỏ qua trong im lặng, hoặc bị đánh dấu xong bằng cách nói dối. Cả hai đều tệ hơn việc sửa câu chữ.

### 2.4 Xoá `Nvm.BusProbe` sẽ xoá luôn bằng chứng D1/D2/D4 của M1

[`M1-factory-model-bus.md`](M1-factory-model-bus.md) §8 dặn: *"M2 có `EdgeGateway` và `Ingestion` thật. Lúc đó probe worker hết vai trò: xoá nó."*

Nhưng ba lệnh DoD của M1 — `make bus-fanout`, `make bus-dlq`, `make bus-chaos` — đều gọi [`src/Workers/Nvm.BusProbe/bus-lab.sh`](../../src/Workers/Nvm.BusProbe/bus-lab.sh), và `bus-lab.sh` khởi động chính worker đó. Xoá worker là làm ba lệnh DoD của một milestone đã đóng **không chạy lại được**.

**Xử lý**: xoá ở **C18**, sau khi Ingestion có consumer thật, và trong cùng commit đó **chuyển `bus-lab.sh` sang consumer thật** thay vì xoá kèm. Một DoD chạy lại được là tài sản; nó là thứ phát hiện regression ở M5 khi ai đó đổi topology. Chi tiết ở §5.C18.

---

## 3. Bốn quyết định đã chốt

Chốt theo hai tiêu chí, xếp theo thứ tự: **học được nghiệp vụ lẫn kỹ thuật của domain này**, và **sát production**. Khi hai tiêu chí kéo ngược nhau, cách xử lý là **tự viết phần đáng học, rồi bọc nó bằng phép kiểm mà production đòi** — không phải chọn một trong hai.

| # | Câu hỏi | Chốt | Ghi ở |
|---|---|---|---|
| Q1 | Gateway → Ingestion đi bằng gì trong `dmz-net`? | **HTTP POST batch, body protobuf, tôn trọng `Retry-After`** | `ADR-027`, C08 |
| Q2 | Decode Sparkplug bằng thư viện hay tự sinh từ `.proto`? | **Tự sinh từ `sparkplug_b.proto`** | `ADR-026`, C01 |
| Q3 | Buffer store-and-forward lưu bằng gì? | **File append-only tự viết + bộ test crash-consistency** | `ADR-028`, C09 |
| Q4 | Dedup store nằm ở PostgreSQL hay SQL Server? | **PostgreSQL/TimescaleDB, cùng transaction với telemetry** | C12 |

### 3.1 Q1 — Gateway → Ingestion đi bằng gì

| Phương án | Được | Mất |
|---|---|---|
| **A. HTTP POST batch (chốt)** | Debug được bằng `curl` từ `make dmz-shell`; backpressure **hiện ra thành mã trạng thái** `429`/`503` + `Retry-After`; Ingestion đã là process ASP.NET Core | Gateway phải tự retry và tự buffer — nhưng nó **phải** làm việc đó rồi, vì store-and-forward là yêu cầu của D3 |
| B. gRPC streaming | Nhanh hơn, flow control sẵn ở tầng HTTP/2 | Flow control của gRPC **che mất** chính thứ M2 tồn tại để nhìn thấy. Backpressure trở thành hành vi của thư viện thay vì một quyết định đọc được trong code |
| C. MQTT vòng hai (gateway publish ngược lên EMQX) | Không thêm giao thức | Biến broker thiết bị thành hàng đợi ứng dụng. `NDEATH`/`bdSeq` của thiết bị sẽ lẫn với message của chính hệ thống, và lúc điều tra sự cố không ai phân biệt được |

**Vì sao A phục vụ cả hai tiêu chí**: cơ chế sống sót của D3 là **buffer trên đĩa của gateway**, không phải flow control của giao thức. Chọn giao thức có flow control tinh vi tạo cảm giác an toàn giả — và khi lab §5.C10.2 chạy, sẽ không biết cái gì đã cứu mình. Đây cũng đúng cách các edge agent production làm (Azure IoT Edge upstream, Telegraf, Fluent Bit): HTTP + batch + backoff, vì nó là thứ **vận hành được bởi người trực đêm**.

> [!warning] Chặng này chưa có xác thực, và đó là một khoảng trống có thật
> Gateway ở `dmz-net` POST vào Ingestion **không kèm credential nào** ở M2. Production không chấp nhận điều đó: `dmz-net` là vùng đệm, và bất cứ thứ gì đứng được ở đó cũng bơm được dữ liệu giả vào hồ sơ traceability.
>
> **Không** vá tạm bằng API key hard-code — đó là K13 vi phạm ngay trong commit vá. Đường đúng là **mTLS giữa gateway và ingestion**, và nó thuộc M13 (*Hardening*) cùng lượt với phần bảo mật còn lại. `ADR-027` §Consequences phải ghi khoảng trống này thành một dòng, để M13 nhặt chứ không phải tự nhớ.

### 3.2 Q2 — Thư viện Sparkplug hay tự sinh

`scope.md` §7.1 để mở: *"cần thư viện `SparkplugNet` hoặc tự sinh code từ `sparkplug_b.proto`"*.

**Chốt tự sinh.** `sparkplug_b.proto` là **một file ~120 dòng**, `Google.Protobuf` + `Grpc.Tools` sinh code lúc build. Đây cũng là cách production làm: bản `.proto` của Eclipse Tahu **chính là** đặc tả, nên sinh từ nó là bám sát chuẩn hơn là tin một wrapper.

Điều thư viện làm hộ mà tự sinh không có: quản lý vòng đời `bdSeq`, theo dõi `seq` gap, phát rebirth. Nhưng ba thứ đó **là nghiệp vụ của M2** (C11) — chúng trả lời *"thiết bị nào đang sống, và tôi đã bỏ lỡ gì"*, câu hỏi của người vận hành chứ không phải của lập trình viên. Giao cho thư viện là giao đi đúng phần đáng học, và cũng là phần mà lúc sự cố ta cần đọc được.

Kiểm license trước khi pin (§2.2): Tahu là EPL-2.0, `Google.Protobuf` là BSD-3. Ép bằng test như `MassTransitPinTests`.

### 3.3 Q3 — Buffer trên đĩa

**Chốt tự viết: file append-only + con trỏ đọc.** Không LiteDB, không SQLite, không RocksDB.

Đây là quyết định mà hai tiêu chí kéo ngược nhau mạnh nhất, nên nói thẳng cả hai chiều:

| | |
|---|---|
| **Vì sao tự viết** | Store-and-forward là **mối quan tâm định nghĩa** của một edge gateway trong IIoT. Viết nó ra thì học được fsync, torn write, CRC, và crash recovery — bốn thứ mà cấu hình SQLite sẽ giấu đi. Và mọi trạng thái nhìn thấy được bằng `ls` và `xxd`, điều lab §5.C10.2 cần khi phải chứng minh 0 mất bằng cách đếm byte |
| **Vì sao đó là chỗ nguy hiểm** | Hàng đợi bền vững tự viết là nơi hệ thống production hay chết: fsync đặt sai chỗ, bản ghi ghi dở khi mất điện, con trỏ đọc và dữ liệu lệch nhau sau crash. "Chạy được trên máy tôi" ở đây không chứng minh gì cả |

**Cách giữ cả hai**: tự viết, nhưng **bọc bằng đúng phép kiểm mà một đội production sẽ đòi khi họ tự sở hữu một thành phần bền vững** — một bộ test crash-consistency chạy trong CI, không phải một lần thử tay. Chi tiết ở C09; đây là phần làm cho quyết định này production-ready thay vì liều.

Định dạng: mỗi bản ghi `[length][payload][crc32]`, một file con trỏ đọc riêng, fsync theo lô. Bản ghi cuối không đọc trọn → **cắt bỏ và đếm**, không cố sửa.

> [!important] `ADR-028` phải ghi **điều kiện thay thế**, không chỉ ghi lý do chọn
> Một quyết định "tự viết" mà không nói khi nào thì thôi tự viết là một món nợ không có ngày đáo hạn. Ba dấu hiệu bắt buộc mở lại quyết định, ghi thành §Consequences:
> 1. Cần **đọc lại** dữ liệu trong buffer theo điều kiện (không chỉ đọc tuần tự) — lúc đó ta đang tự viết một database, và nên dừng.
> 2. Cần **nhiều consumer** đọc cùng buffer với con trỏ riêng.
> 3. Bộ test crash-consistency ở C09 bắt đầu **đỏ ngẫu nhiên** — dấu hiệu mô hình bền vững tự viết đã vượt quá thứ ta suy luận nổi.

### 3.4 Q4 — Dedup store ở đâu

**Chốt PostgreSQL/TimescaleDB**, đúng DDL `scope.md` §7.2 đã viết sẵn.

Lý do đặt lại cho rõ: bảng `ingest.processed_message` phải commit **cùng transaction** với chính bản ghi telemetry mà nó bảo vệ (`ADR-023` §Alternatives, dòng đầu). Telemetry ở TimescaleDB, nên khoá dedup cũng phải ở đó. Đặt nó ở SQL Server là dựng lại đúng dual-write mà `ADR-022` đã đo thiệt hại — chỉ khác chỗ.

> [!important] Đây là lần đầu trong dự án khoá dedup commit được cùng effect nó bảo vệ
> `ADR-023` nói K7 chỉ đúng trong một process cho tới M5, vì effect duy nhất ở M1 là một dictionary trong RAM: *"một chỗ giữ bền vững canh một effect không bền vững thì tệ hơn, không tốt hơn"*.
>
> Ở tầng ingestion thì điều kiện đó **đã đủ** — effect là một `INSERT`, và transaction bao được cả hai. Nên M2 đóng được ràng buộc này ở tầng của nó, trong khi command pipeline vẫn chờ M5. Hai tầng dedup, hai lịch trình, và `scope.md` §7.2 nói đúng khi gọi đó là *"hai tầng, không phải một"*. C12 phải nhắc lại điều này vào `ADR-023` §Consequences khi xong.

## 4. Tổng quan 19 commit

| # | Commit message | Giai đoạn | Ước lượng | Phụ thuộc |
|---|---|---|---|---|
| C01 | `build(ingestion): add sparkplug b proto and pin protobuf toolchain` | A — Contract & decode | 60' | — |
| C02 | `feat(ingestion): decode sparkplug payload into device readings` | A | 90' | C01 |
| C03 | `feat(ingestion): map sparkplug topic onto equipment path` | A | 60' | C02 |
| C04 | `feat(ingestion): derive source event id from the natural key` | A | 60' | C03 |
| C05 | `feat(simulator): add formation cycler on compressed time` | B — Simulator | 90' | C01 |
| C06 | `feat(simulator): add duplicate, dropout and clock drift faults` | B | 90' | C05 |
| C07 | `build(simulator): run the simulator inside ot-net` | B | 60' | C05 |
| C08 | `feat(edge-gateway): subscribe emqx and stamp the gateway timestamp` | C — Edge gateway | 90' | C02, C07 |
| C09 | `feat(edge-gateway): add append-only store-and-forward buffer` | C | 120' | C08 |
| C10 | `feat(edge-gateway): flush with rate limit and honour backpressure` | C | 90' | C09 |
| C11 | `feat(edge-gateway): track nbirth, ndeath and sequence gaps` | C | 90' | C08 |
| C12 | `feat(ingestion): add processed message table and dedup on insert` | D — Ingestion | 90' | C04 |
| C13 | `feat(ingestion): classify clock quality from the two timestamps` | D | 60' | C12 |
| C14 | `feat(ingestion): publish canonical cloudevents onto the bus` | D | 75' | C12 |
| C15 | `feat(ingestion): accept csv file drop on the same dedup path` | D | 75' | C12 |
| C16 | `feat(tools): add mqtt load harness for five thousand msg per second` | E — Đo và đóng | 90' | C10, C14 |
| C17 | `test(ingestion): add one hour reconciliation with ten percent duplicates` | E | 120' | C16 |
| C18 | `refactor(bus): point the bus lab at real consumers and drop the probe` | E | 30' | C14 |
| C19 | `docs: close m2 with benchmarks and checklist` | E | 45' | tất cả |

**Tổng: 1485 phút = 24 giờ 45 phút.** Với 10–12 giờ/tuần thì M2 rơi vào **2,1–2,5 tuần**, khớp `scope.md`. Nếu tới ngày thứ 10 mà chưa qua C12 thì cắt theo R-M2-8.

Giai đoạn A làm được cả lúc Docker tắt. B, C, D, E **bắt buộc** `make up`, và từ C07 trở đi bắt buộc chạy trong container — không `dotnet run` trên Windows rồi nối `localhost:1883` (§2.1, `scope.md` §13.1).

> [!important] Nhắc lại từ AGENTS.md §1.1
> Agent **không tự commit**. Xong mỗi C, dừng lại, báo cáo, bạn tự đọc `git diff` rồi commit. Commit message ở cột trên là **đề xuất**.

> [!important] Nhắc lại từ AGENTS.md §5.8
> Mỗi commit **mở đầu** bằng khối **Nghiệp vụ** (khái niệm mới · vì sao có mặt · nếu làm sai thì hỏng gì trên dây chuyền), **rồi mới** code. M2 là milestone nghiệp vụ nặng nhất từ đầu dự án — `formation`, `NBIRTH`, `bdSeq`, `report-by-exception`, `clock drift` đều là từ vựng nhà máy, không phải từ vựng lập trình. Từ nào chưa có trong [`glossary.md`](../glossary.md) thì thêm **trong chính commit đó**.

> [!note] ADR viết tại chỗ quyết định
> `ADR-026` trong **C01**, `ADR-027` trong **C08**, `ADR-028` trong **C09**, `ADR-029` trong **C10**. `ADR-010` nhận thêm §Evidence trong **C13.3**.

---

## 5. Chi tiết từng commit

**Chỉ mục lab phá hoại** — bốn lab, và chúng đánh số theo hai hệ khác nhau, nên tra ở đây thay vì đoán:

| Lab | Ở đâu | Nguồn | Đo cái gì |
|---|---|---|---|
| Tắt backend 2 phút | §5.C10.2 | **DoD D3** của plan này | Store-and-forward có giữ được 0 mất không |
| Xả buffer 30 phút | §5.C10.3 | `scope.md` lab **#3** | `ADR-029` có đáng tồn tại không |
| Bỏ `device_timestamp` khỏi natural key | §5.C13.2 | `scope.md` lab **#2** | Dedup sai **không** báo lỗi — nó báo thành công và trả về ít hơn |
| Bỏ dedup | §5.C13.3 | `scope.md` lab **#1** | Số bản ghi thừa → **`ADR-010` §Evidence** |

Cộng thêm một bộ kiểm chạy trong CI chứ không phải lab một lần: `make buffer-crash` (§5.C09.1), 200 vòng `kill -9`.


### C01 — `build(ingestion): add sparkplug b proto and pin protobuf toolchain`

**Mục tiêu**: có `sparkplug_b.proto` trong repo và sinh được C# từ nó lúc build, với license đã kiểm.

**Việc làm**
- `src/Platform/Nvm.Sparkplug/` — project mới ở tầng **Platform**, không phải Functional Block. Nó là hạ tầng giải mã, dùng chung cho gateway lẫn test.
- `proto/sparkplug_b.proto` lấy từ đặc tả Eclipse Tahu, **commit vào repo** kèm URL và ngày tải trong comment đầu file. Không tải lúc build.
- `Google.Protobuf` + `Grpc.Tools` vào `Directory.Packages.props`. `Grpc.Tools` chỉ là build-time (`PrivateAssets="all"`).
- `ADR-026` — *Tự sinh từ `.proto` thay vì dùng thư viện Sparkplug*. §Alternatives nêu `SparkplugNet` và lý do loại (§3.2).

**Kiểm chứng**
```bash
make build
```
Test bắt buộc: decode được một payload `.bin` **thật** đã lưu sẵn trong `tests/` ra đúng số metric — không phải payload do chính code này encode ra rồi decode lại. Một round-trip tự-encode-tự-decode xanh cả khi cả hai chiều cùng sai.

Kiểm license **trước** khi pin, ghi kết quả vào `ADR-026` §Context: bản Tahu là EPL-2.0, `Google.Protobuf` là BSD-3. Ép bằng test như `MassTransitPinTests` của M1/C09.

---

### C02 — `feat(ingestion): decode sparkplug payload into device readings`

**Mục tiêu**: từ `byte[]` ra một danh sách phép đo có kiểu, không còn `Any`/`object`.

**Việc làm**
- `SparkplugPayload` → `IReadOnlyList<DeviceReading>`. `DeviceReading` có `MetricName`, `Value`, `DeviceTimestamp` (`DateTimeOffset`, K2), `Alias`.
- Sparkplug cho phép metric dùng **alias** thay tên sau `NBIRTH` — decode phải tra bảng alias, và **ném** khi gặp alias chưa từng thấy trong `NBIRTH`.
- `ImmutableArray` cho mọi collection lộ ra ngoài (`ADR-025`).

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: payload có alias mà chưa có `NBIRTH` → ném, **không** trả danh sách rỗng (cùng bài học với `CloudEventContextExtensions`: *không đọc được* khác *không có*); `DateTimeOffset` chứ không `DateTime` ở mọi field thời gian; metric kiểu `Double`, `Int64`, `Boolean`, `String` đều ra đúng giá trị.

#### C02.1 — Nghiệp vụ: vì sao Sparkplug dùng alias, và vì sao alias lạ phải ném

Một máy formation có 1.000 kênh, mỗi kênh 6–8 metric. Gửi tên metric đầy đủ (`FORM-01-CH-0142/voltage`) trong **mỗi** `DDATA` là gửi vài trăm byte tên cho vài byte dữ liệu, 5.000 lần một giây.

Sparkplug giải bằng **alias**: `NBIRTH` khai báo một lần *"metric 17 tên là voltage"*, sau đó `DDATA` chỉ gửi `17`. Đây là cùng ý tưởng với **report-by-exception** — nói ít nhất có thể, vì đường truyền OT hẹp và máy thì nhiều.

Hệ quả: **alias chỉ có nghĩa trong ngữ cảnh một phiên `NBIRTH`**. Node khởi động lại, `NBIRTH` mới có thể gán `17` cho một metric khác. Nếu decode gặp alias lạ mà đoán bừa hoặc bỏ qua, dữ liệu vào DB sẽ mang **đúng hình dạng nhưng sai ý nghĩa** — điện áp của kênh này ghi vào nhiệt độ của kênh kia. Không có gì báo lỗi, và chỉ lộ ra khi kỹ sư quy trình nhìn biểu đồ thấy nhiệt độ 3,7.

Đó là lý do alias lạ phải **ném và xin rebirth** (C11), không phải bỏ qua.

---

### C03 — `feat(ingestion): map sparkplug topic onto equipment path`

**Mục tiêu**: nối thế giới Sparkplug vào thế giới ISA-95 mà M1 đã dựng.

**Việc làm**
- `spBv1.0/{group_id}/{type}/{edge_node}/{device}` → `EquipmentPath` của M1/C06.
- Ánh xạ theo `scope.md` §7.1: `group_id` = `{Enterprise}-{Site}-{Area}`, `edge_node_id` = `EDGE-{Line}`, `device_id` = equipment code.
- Chiều ngược lại cũng phải có (`EquipmentPath` → topic), vì simulator ở C05 cần nó. **Một hàm sinh, một hàm phân tích, và một test round-trip qua cả hai.**

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: `spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142` ra đúng `NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142`; topic có site **không có trong factory model** bị từ chối (K3); phân biệt HOA/thường đúng — `scope.md` §7.1 dùng HOA cho Sparkplug và UNS dùng thường, đây là chỗ y hệt cái bẫy `EventSource`/`RoutingKey` của M1/C02.1.

---

### C04 — `feat(ingestion): derive source event id from the natural key`

**Mục tiêu**: một phép đo có **một** danh tính, suy ra được từ chính nó.

**Việc làm**
- Natural key của measurement theo `scope.md` §7.2: `(site_id, equipment_id, unit_id, step_code, device_timestamp, signal_code)`.
- Dùng lại `IdempotencyKey.FromNaturalKey` và `DeterministicGuid.CreateVersion5` của M1/C04 — **không viết lại**. Đây là lúc kiểm rằng thứ M1 dựng có dùng được thật không.
- `device_timestamp` vào khoá phải ở **một** định dạng chuẩn hoá duy nhất (round-trip, UTC). Hai cách viết cùng một thời điểm cho ra hai GUID khác nhau, và dedup mất tác dụng mà không báo gì.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: cùng phép đo qua hai lần decode ra **cùng** GUID; đổi **một** thành phần bất kỳ của natural key ra GUID khác; `device_timestamp` viết ở hai offset khác nhau nhưng cùng một instant ra **cùng** GUID.

> Test thứ ba là test đắt nhất trong ba. Nó chính là lab phá hoại #2 của `scope.md` §9/M2 viết ngược lại thành regression.

---

### C05 — `feat(simulator): add formation cycler on compressed time`

**Mục tiêu**: có nguồn dữ liệu thật để chạy mọi thứ còn lại, và nó phải **nén được thời gian**.

**Việc làm**
- `src/Workers/Nvm.Simulator/` — 1.000 kênh formation trên `NV1`, phát `NBIRTH` rồi `DDATA` theo report-by-exception.
- **`TimeProvider` cho mọi thứ liên quan thời gian** (K1). Nén thời gian là nhân hệ số vào `TimeProvider`, không phải `Thread.Sleep` ngắn lại — một chu kỳ formation thật kéo 18 giờ, và test không được chờ 18 giờ.
- Metric có ý nghĩa vật lý: điện áp bò lên theo đường sạc, dòng bậc thang theo giai đoạn CC/CV, nhiệt độ trôi theo dòng. **Không** random thuần.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: nén 1.000× thì một chu kỳ 18 giờ chạy xong trong test tính bằng giây, và **số message sinh ra không đổi** so với chạy thật — nén thời gian không được làm mất hay thêm phép đo.

#### C05.1 — Nghiệp vụ: `formation` là gì và vì sao nó dài 18 giờ

Sau khi cell được cuốn và đổ điện giải, nó **chưa phải pin**. Lớp màng bảo vệ ở cực âm — **SEI** — chưa hình thành. `Formation` là lần sạc–xả đầu tiên, chạy chậm và có kiểm soát, để lớp màng đó hình thành đều.

Ba điều làm nó quan trọng với MES:

1. **Nó dài.** 12–24 giờ mỗi cell. Nhà máy chạy hàng nghìn kênh song song, và một kênh hỏng giữa chừng là 18 giờ WIP mất trắng. Đây là lý do M7 có saga nhiều ngày, và là lý do K1 cấm `DateTime.UtcNow` ngay từ M0.
2. **Nó là phép đo chất lượng đầu tiên.** Đường cong điện áp trong formation dự đoán tuổi thọ cell. Cell có đường cong lệch bị tách ra trước khi vào module — rẻ hơn nhiều so với phát hiện ở pack.
3. **Dữ liệu của nó là dữ liệu dày nhất trong nhà máy.** 1.000 kênh × 6 metric × mỗi vài giây. Con số 5.000 msg/s của N1 đến từ đây, không phải từ ước lượng.

**Nếu simulator sinh dữ liệu random thay vì có hình dạng vật lý**: mọi thứ hạ nguồn vẫn "chạy", nhưng không ai phát hiện được khi projection ở M3 tính sai đường cong, vì không có đường cong đúng để so. Lỗi sẽ ngủ tới M8.

---

### C06 — `feat(simulator): add duplicate, dropout and clock drift faults`

**Mục tiêu**: simulator phải **gửi bậy có chủ đích**, vì đó là thứ M2 tồn tại để chịu đựng.

**Việc làm**
Ba fault, bật/tắt được bằng config, mặc định **bật** khi chạy lab:
- **Trùng 10%**: gửi lại đúng message đã gửi, kể cả `seq`. Không phải sinh message mới giống nhau — phải là **cùng một** message.
- **Ngắt kết nối ngẫu nhiên**: mất kết nối MQTT, đệm nội bộ, rồi **xả cả cụm** khi nối lại.
- **Lệch đồng hồ ±2 giờ ở 10% thiết bị**: `device_timestamp` sai, mọi thứ khác đúng.
- Simulator **đếm số phép đo logic** đã sinh và ghi ra file — đây là vế trái của phép đối chiếu ở D1.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: bật fault trùng 10% trên 10.000 message → đếm được **đúng** ~1.000 bản trùng và số phép đo **logic** vẫn là 10.000; bản trùng có **cùng** `source_event_id` với bản gốc (nếu khác thì fault này đang giả lập sai thứ, và D1 sẽ xanh giả).

> Chỗ dễ sai nhất của cả M2 nằm ở dòng cuối. Một "duplicate" sinh ra với timestamp mới **không phải** duplicate — nó là một phép đo khác. Dedup sẽ đúng khi để nó qua, và D1 sẽ báo đạt trong khi chưa kiểm gì.

---

### C07 — `build(simulator): run the simulator inside ot-net`

**Mục tiêu**: simulator chạy đúng chỗ nó phải chạy, không phải chỗ tiện.

**Việc làm**
- `Dockerfile` cho `Nvm.Simulator`, service trong `docker-compose.yml` với `networks: [ot-net]`, profile riêng để `make up` thường không kéo nó lên.
- `make sim-up` / `make sim-down`.

**Kiểm chứng**
```bash
make net-check
```
**9/9 vẫn phải xanh** sau khi thêm service. Và một phép đo mới: từ container simulator, `connects emqx 1883` phải **mở**, `connects rabbitmq 5672` phải **đóng**.

> [!danger] Đây là chỗ ranh giới OT/IT dễ thủng lại nhất
> `scope.md` §13.1 nói thẳng: chạy `dotnet run` trên Windows rồi nối `localhost:1883` là **tự tay dựng lại** đúng lỗ hổng mà M0/R1 đã bịt — vì máy Windows là tầng IT. EMQX đã bỏ `ports:` chính vì chuyện này.
>
> Nếu trong lúc phát triển thấy bất tiện, câu trả lời là `make dmz-shell`, không phải mở lại port.

---

### C08 — `feat(edge-gateway): subscribe emqx and stamp the gateway timestamp`

**Mục tiêu**: có một process ở `dmz-net` nghe được thiết bị và gắn dấu thời gian đáng tin.

**Việc làm**
- `src/Workers/Nvm.EdgeGateway/`, `networks: [dmz-net]`, Dockerfile.
- Subscribe `spBv1.0/#`, decode bằng `Nvm.Sparkplug` (C02), gắn `gateway_timestamp` từ `TimeProvider` (K1).
- Gửi sang Ingestion bằng **HTTP POST batch** theo Q1. Chưa có buffer ở commit này — gửi thẳng, lỗi thì mất. Buffer vào ở C09.
- `ADR-027` — *Chặng gateway → ingestion đi bằng HTTP trong `dmz-net`*. §Alternatives nêu gRPC và MQTT-vòng-hai (§3.1). §Consequences **bắt buộc** có một dòng: chặng này **chưa xác thực**, và đường đúng là mTLS ở **M13**, không phải API key hard-code (K13). Ghi thành nợ có địa chỉ, không để M13 tự nhớ.

**Kiểm chứng**
```bash
make net-check && make sim-up
```
Từ `make dmz-shell`: thấy message trên EMQX. Log gateway: đếm số message decode được. Và **`connects rabbitmq 5672` từ container gateway phải đóng** — nếu mở thì §2.1 đã bị lách chứ không được giải.

---

### C09 — `feat(edge-gateway): add append-only store-and-forward buffer`

**Mục tiêu**: gateway sống sót khi backend chết, và **chứng minh được** là không mất gì.

**Việc làm**
- Buffer file append-only theo Q3: mỗi bản ghi `[length][payload][crc32]`, một file con trỏ đọc riêng, fsync theo lô.
- Bản ghi cuối không đọc trọn (mất điện giữa lúc ghi) → **cắt bỏ và log**, không cố sửa.
- Xoay file theo kích thước; giữ ngưỡng đĩa tối đa, chạm ngưỡng thì **dừng nhận và log**, không âm thầm ghi đè bản cũ nhất.
- Metric `gateway.buffer.depth` và `gateway.buffer.bytes` expose ngay từ commit này. Một buffer không đo được độ sâu là một buffer không ai biết đang đầy tới đâu cho tới lúc nó đầy.
- `ADR-028` — *Buffer tự viết thay vì embedded database*, kèm **ba điều kiện thay thế** ở §Consequences (§3.3).

**Kiểm chứng**
```bash
make test && make buffer-crash
```

Test thường: ghi 10.000 bản ghi, mở lại → đọc đủ; CRC sai → bản ghi đó bị bỏ và **được đếm**, không im lặng; chạm ngưỡng đĩa → ngừng nhận, không ghi đè.

#### C09.1 — Bộ test crash-consistency: phần làm cho "tự viết" thành production-ready

Đây là điều kiện §3.3 đặt ra, và nó là **một target riêng trong `make ci`**, không phải một lần thử tay.

`make buffer-crash` chạy **N = 200 vòng**, mỗi vòng:

1. Ghi một số bản ghi ngẫu nhiên (1–500), fsync ở một điểm ngẫu nhiên.
2. `kill -9` process ghi tại một thời điểm ngẫu nhiên — **không** `Dispose`, không finally, không graceful shutdown.
3. Mở lại buffer, đọc hết.
4. Assert **ba bất biến**:
   - Mọi bản ghi đã **fsync xong** đều đọc lại được — không mất.
   - Không bản ghi nào đọc ra **sai nội dung** — CRC bắt được mọi torn write.
   - Con trỏ đọc **không bao giờ vượt** phần dữ liệu hợp lệ — không đọc rác.

Ba bất biến đó, không phải "chạy thấy ổn", mới là thứ phân biệt một buffer bền vững với một file được ghi vào.

> [!important] `kill -9`, không phải `Dispose()`
> Test gọi `Dispose()` rồi mở lại chỉ chứng minh đường **đóng sạch** hoạt động — đúng con đường mà mất điện **không** đi. Cùng loại lỗi với test tự-stamp-rồi-tự-assert của M1/C12: nó xanh vì nó không đi qua chỗ hỏng.

Số vòng đỏ (kỳ vọng **0/200**) vào `benchmarks.md`. Nếu bộ này bắt đầu đỏ **ngẫu nhiên** thì đó là điều kiện thứ ba của `ADR-028` — dừng tự viết, chuyển sang embedded store.

> [!important] Ngưỡng đĩa đầy là quyết định nghiệp vụ, không phải chi tiết kỹ thuật
> Khi buffer đầy có hai lựa chọn: **bỏ dữ liệu cũ nhất** hay **ngừng nhận dữ liệu mới**. Với traceability, dữ liệu cũ là hồ sơ đã có một phần — bỏ nó tạo ra lỗ hổng giữa chừng mà auditor sẽ tìm thấy. Dữ liệu mới thì máy vẫn còn đó và có thể hỏi lại. Nên: **ngừng nhận, kêu to**. Ghi vào `ADR-028` §Consequences.

---

### C10 — `feat(edge-gateway): flush with rate limit and honour backpressure`

**Mục tiêu**: khi backend sống lại, gateway **không** giết nó bằng chính đống dữ liệu vừa giữ hộ.

**Việc làm**
- Flush có rate limit; Ingestion trả `429`/`503` + `Retry-After` thì gateway **giảm tốc**, không retry ngay.
- Backoff có **jitter** — cùng lý do `NvmRetryPolicy` của M1/C11.1 có: n gateway nối lại cùng lúc mà backoff giống hệt nhau thì chúng cũng thử lại cùng lúc, và đó chính là retry storm.
- Metric `gateway.flush.rate` và `gateway.flush.throttled` — để lab §5.C10.2 đọc được **vì sao** backlog tiêu nhanh hay chậm, không chỉ đọc được bao lâu.
- `ADR-029` — *Rate limit khi flush, và vì sao gateway phải chậm lại thay vì thử lại nhanh hơn*.

**Kiểm chứng**: lab §5.C10.2.

#### C10.1 — Nghiệp vụ: vì sao "xả hết thật nhanh" là phản xạ sai

Trực giác nói: backend lên rồi, đẩy hết đi cho nhanh, càng sớm càng an toàn.

Thực tế nhà máy: mạng OT vừa chớp **không chỉ** ảnh hưởng một gateway. Cả khu `FORMATION` mất mạng cùng lúc, và cả khu nối lại cùng lúc. Mười gateway xả đệm 2 giờ cùng một nhịp là một cuộc tấn công DDoS vào chính hệ thống của mình — và nó xảy ra **đúng lúc** hệ thống vừa khởi động lại, tức lúc yếu nhất.

Hệ quả nếu làm sai: ingestion sập lần hai, gateway thấy lỗi nên retry nhanh hơn, và vòng lặp tự siết. Đây là **retry storm**, và nó biến một sự cố mạng 30 giây thành một sự cố hệ thống nửa tiếng.

#### C10.2 — Lab D3: tắt toàn bộ backend 2 phút giữa lúc simulator chạy

Đây là **D3**. Nó **không** nằm trong ba lab đánh số của `scope.md` §9/M2 — nó là phép kiểm của chính DoD. Bảng chỉ mục lab ở đầu §5.

> [!important] Đoán trước khi đo (`AGENTS.md` §5.8.4)
> Trước khi chạy, agent hỏi chủ repo đoán **ba** con số, ghi lại, rồi mới chạy:
> 1. Bao nhiêu message nằm trên đĩa sau 2 phút tắt?
> 2. Backlog tiêu hết trong bao lâu sau khi bật lại?
> 3. Simulator có tự dừng không, và nếu có thì sau bao lâu?

Các bước:
1. `make sim-up`, chạy ổn định 2 phút, ghi số message đã gửi.
2. `docker compose stop` ingestion + timescale + rabbitmq. **Không** `make down-v` (`AGENTS.md` §1.2).
3. Chờ đúng 120 giây. Ghi: simulator còn chạy không · `ls -l` thư mục buffer của gateway · `/health/live` của gateway.
4. Bật lại. Đo thời gian tới khi buffer rỗng.
5. Đối chiếu: **số phép đo logic simulator sinh ra** so với **số row trong DB**.

Bốn con số vào `benchmarks.md` và `ADR-028`: số message trên đĩa · thời gian tiêu backlog · số row lệch (phải là **0**) · số lần simulator restart (phải là **0**, đó là N15).

> **Không được làm tròn số lệch về 0.** Nếu nó khác 0 thì D3 chưa đạt, và điều đó phải được ghi đúng như M1 đã ghi 18/200 (`AGENTS.md` §1.3).

#### C10.3 — Lab phá hoại #3: buffer 30 phút rồi xả cùng lúc

Lab thứ ba của `scope.md` §9/M2, và là lab **duy nhất** đo được `ADR-029` có đáng tồn tại không. C10.2 chỉ tắt backend 2 phút — buffer nhỏ, xả xong trước khi ai kịp thấy gì.

> [!important] Đoán trước khi đo (`AGENTS.md` §5.8.4)
> Ba con số, ghi lại trước khi chạy:
> 1. 30 phút ở 5.000 msg/s là bao nhiêu message, và bao nhiêu MB trên đĩa?
> 2. Xả **không** rate limit thì ingestion trụ được bao lâu trước khi p95 lag vượt 5 s?
> 3. Xả **có** rate limit thì tiêu hết backlog mất bao lâu?

Các bước:
1. Chạy simulator ở tốc độ N1. Tắt ingestion **30 phút** — không tắt EMQX, không tắt simulator.
2. Ghi `gateway.buffer.depth` và `gateway.buffer.bytes` lúc cuối.
3. **Chạy hai lần**, cùng một buffer đã đầy (sao lưu thư mục buffer trước lần một để lần hai xuất phát y hệt):
   - **Lần A — rate limit TẮT**: đo p95 lag của ingestion, CPU, và có `429`/`503` nào không. Ingestion sập hay không sập, ghi đúng như đo được.
   - **Lần B — rate limit BẬT**: đo thời gian tiêu backlog và số lần bị throttle.
4. Đối chiếu số row cuối cùng ở cả hai lần: **phải bằng nhau và bằng số phép đo logic**. Rate limit được phép làm chậm, **không** được phép làm mất.

Bốn con số vào `benchmarks.md` và `ADR-029`: kích thước buffer sau 30 phút · p95 lag lần A · thời gian tiêu backlog lần B · số row lệch (phải là **0** ở cả hai lần).

> Giá trị của lab này nằm ở **lần A**. Nếu lần A cũng không sao, thì `ADR-029` đang giải một vấn đề chưa tồn tại ở quy mô này — và điều đó phải được ghi thẳng vào ADR, đúng cách `AGENTS.md` §3.2 yêu cầu, thay vì giữ rate limit chỉ vì nó là một pattern nổi tiếng. Cùng bài học với closure table ở §3.2 của `AGENTS.md`.

---

### C11 — `feat(edge-gateway): track nbirth, ndeath and sequence gaps`

**Mục tiêu**: biết được thiết bị nào đang sống, và biết khi nào mình đã bỏ lỡ dữ liệu.

**Việc làm**
- `NBIRTH` → reset bảng alias của node, đánh dấu metric "đã biết", ghi `bdSeq`.
- `NDEATH` (qua MQTT Last Will) → mọi metric của node chuyển **`STALE`**. **Không** xoá dữ liệu lịch sử — đây là D4 và là K4.
- `seq` nhảy cóc → phát **rebirth request**, và **đếm** số lần. Thêm **rebirth** vào `glossary.md`.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: `NDEATH` → trạng thái `STALE` và `SELECT count(*)` trên telemetry **không đổi**; `seq` từ 5 nhảy sang 7 → đúng **1** rebirth request; `NBIRTH` với `bdSeq` mới → bảng alias cũ bị bỏ hoàn toàn, alias cũ không còn giải được.

#### C11.1 — Nghiệp vụ: `STALE` khác `null` và khác `0`

Một kênh formation ngừng gửi dữ liệu. Ba cách hệ thống có thể hiểu, và chúng dẫn tới ba hành động khác nhau trên sàn:

| Hệ thống ghi | Người vận hành đọc thành | Hành động |
|---|---|---|
| `0` | Điện áp bằng 0 | **Báo động sai** — tưởng cell chết, dừng kênh, gọi kỹ thuật |
| `null` | Chưa có dữ liệu | Bỏ qua, tưởng kênh chưa bắt đầu |
| **`STALE`** | Số cuối cùng biết được là X, lúc T, và từ đó **không còn tin được** | Đi kiểm **kết nối**, không kiểm cell |

`NDEATH` tồn tại để phân biệt ba thứ đó. Không có nó thì mất mạng và cell hỏng trông giống hệt nhau từ phòng điều khiển.

**Nếu làm sai — xoá dữ liệu khi `NDEATH`**: vi phạm K4, và mất đúng phần dữ liệu quý nhất. Một cell đang chạy formation thì mất mạng, 6 giờ dữ liệu trước đó vẫn là hồ sơ hợp lệ của 6 giờ đó. Xoá nó là xoá bằng chứng.

---

### C12 — `feat(ingestion): add processed message table and dedup on insert`

**Mục tiêu**: tầng dedup thật, và nó phải commit **cùng transaction** với dữ liệu nó bảo vệ.

**Việc làm**
- `src/Workers/Nvm.Ingestion/`, `networks: [dmz-net, it-net]` — điểm bắc cầu duy nhất (§2.1).
- Bảng `ingest.processed_message` theo DDL ở `scope.md` §7.2, partition theo tháng.
- `INSERT ... ON CONFLICT DO NOTHING` trên `source_event_id`, **cùng transaction** với insert telemetry. Số row bị `ON CONFLICT` nuốt được **đếm** và expose thành metric.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc (Testcontainers PostgreSQL): gửi cùng message 3 lần → **1** row telemetry, counter duplicate = **2**; hai message **khác nhau** chỉ lệch `device_timestamp` → **2** row; giết transaction giữa chừng → **không** còn khoá dedup lẫn telemetry (cùng transaction, cùng số phận).

> Test thứ ba là thứ M1 **không** làm được và `ADR-023` đã ghi rõ là chưa đóng. Ở đây đóng được, vì effect là một `INSERT` chứ không phải một dictionary trong RAM. Nhắc lại điều này trong `ADR-023` §Consequences khi C12 xong.

---

### C13 — `feat(ingestion): classify clock quality from the two timestamps`

**Mục tiêu**: nhận dữ liệu từ thiết bị lệch giờ, và **nói ra** là nó lệch.

**Việc làm**
- `clock_quality` = `Good` | `Drifted` | `Unknown` theo `|device_timestamp − gateway_timestamp|`, ngưỡng **5 phút** (`scope.md` §7.3).
- Lệch quá ngưỡng → `Drifted` và **vẫn ghi** (N15 — không làm dừng dây chuyền), không từ chối.
- Lưu **cả ba** timestamp, không ghi đè: `device_timestamp`, `gateway_timestamp`, `recorded_at`.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: lệch 2 giờ → `Drifted` và row **có mặt** (D5); lệch 10 giây → `Good`; thiếu `device_timestamp` → `Unknown`, không ném; cả ba cột đều có giá trị khác nhau trong cùng một row.

> [!warning] Ca *"thiếu `device_timestamp`"* không đến được từ đường Sparkplug — phát hiện ở C02
> C02 **ném** khi metric không có timestamp riêng **và** payload cũng không có, vì `device_timestamp`
> nằm trong natural key (`scope.md` §7.2): một reading thiếu nó sẽ **không bao giờ** dedup được, và
> ghi nó xuống là ghi một phép đo vĩnh viễn không nhận ra chính nó.
>
> Nên `clock_quality = Unknown` chỉ có nguồn thật là đường **file drop** ở C15, nơi không có đồng hồ
> thiết bị nào cả. Khi làm C13, chọn một trong hai: viết test `Unknown` dựa trên C15, hoặc quyết
> ngược lại rằng C02 phải trả `DeviceTimestamp` nullable — và nếu chọn vế sau thì C04 phải nói được
> khoá dedup của một reading không có thời điểm trông như thế nào.

#### C13.1 — Nghiệp vụ: vì sao không từ chối message lệch giờ

Phản xạ kỹ thuật: dữ liệu sai thì không nhận.

Nhưng đồng hồ PLC lệch là chuyện **thường ngày** — pin CMOS hết, NTP không tới được tầng OT, thiết bị vừa thay board. Từ chối message vì lệch giờ nghĩa là: một quả pin CMOS 20 nghìn đồng làm mất toàn bộ hồ sơ traceability của một dây chuyền, và không ai biết cho tới khi auditor hỏi.

`gateway_timestamp` mới là thứ đáng tin, và ta **có** nó. Nên: nhận, ghi cả ba, gắn cờ, và để dashboard hiện badge. Con người sửa đồng hồ; hệ thống không mất dữ liệu trong lúc chờ.

#### C13.2 — Lab phá hoại #2: đổi natural key, bỏ `device_timestamp`

Theo `scope.md` §9/M2 lab #2.

Bỏ `device_timestamp` khỏi natural key → hai phép đo **khác nhau** của cùng một kênh cùng một signal sẽ ra **cùng** `source_event_id`, và dedup nuốt mất một.

Đoán trước khi đo: trên 10.000 phép đo, bao nhiêu bị nuốt? Rồi chạy. Số vào `benchmarks.md`.

Bài học đáng giữ: dedup sai **không** báo lỗi. Nó báo *thành công* và trả về ít dữ liệu hơn. Đây là loại lỗi duy nhất mà "test xanh" hoàn toàn vô nghĩa nếu không có phép đối chiếu số lượng.

#### C13.3 — Lab phá hoại #1: bỏ dedup, đếm bản ghi thừa

Theo `scope.md` §9/M2 lab #1. **Con số vào `ADR-010` §Evidence** — `ADR-010` viết ở M1/C04 và đã ghi rõ chỗ này còn trống.

Bỏ `ON CONFLICT DO NOTHING`, chạy lại kịch bản 10% duplicate, đếm row thừa. Kỳ vọng ~10%, nhưng **đo rồi mới ghi**.

---

### C14 — `feat(ingestion): publish canonical cloudevents onto the bus`

**Mục tiêu**: dữ liệu đã sạch đi tiếp vào hệ thống, đúng envelope M1 đã dựng.

**Việc làm**
- Publish `MeasurementRecorded` (và các event M2 khác) lên bus, dùng `AddNvmBus` + `UseNvmCloudEvents` của M1 — **không** dựng đường publish thứ hai.
- `ce_id` = `source_event_id` của C04. Đây là chỗ dedup tầng thiết bị nối vào dedup tầng command (`scope.md` §7.2).
- Cập nhật `docs/event-catalog.md`.

**Kiểm chứng**
```bash
make test && make bus-dlq
```
Test bắt buộc: `ce_id` đọc lại từ header **bằng** `source_event_id` trong DB (M1/R-M1-6 nói lệch hai giá trị này chỉ lộ ở M2 — đây chính là chỗ nó lộ); event có `[EventVersion(1)]`; publish thất bại **không** làm rollback insert telemetry, và số publish hỏng **được đếm**.

> [!warning] Đây vẫn là dual-write, và M2 không sửa được
> Insert vào Timescale rồi publish lên RabbitMQ là hai việc không có transaction chung. `ADR-022` đã đo: **18/200** mất khi broker chết. M2 **giữ nguyên** giới hạn đó và **đếm** nó, không giả vờ đã đóng. Outbox ở M6. Xem §2.3.

---

### C15 — `feat(ingestion): accept csv file drop on the same dedup path`

**Mục tiêu**: máy test cũ không nói MQTT vẫn vào được hệ thống, và vào **cùng một cửa**.

**Việc làm**
- Watcher trên một thư mục, parse CSV, dựng natural key **giống hệt** C04, đi qua **đúng** đường dedup của C12.
- File đã xử lý chuyển sang `processed/`; file hỏng sang `rejected/` kèm file `.error` nói vì sao.

**Kiểm chứng**
```bash
make test
```
Test bắt buộc: thả **cùng một file** hai lần → số row **không đổi**; file có 1 dòng hỏng giữa 100 dòng → 99 row vào, 1 dòng vào `rejected` với lý do, **không** bỏ cả file.

#### C15.1 — Nghiệp vụ: vì sao adapter cũ phải đi cùng cửa, không đi cửa riêng

Nhà máy thật luôn có máy không nói giao thức hiện đại — máy EOL 15 năm tuổi xuất CSV lên thư mục chia sẻ. Cám dỗ là viết cho nó một đường riêng cho nhanh.

Hậu quả của đường riêng: hai định nghĩa natural key, hai chỗ dedup, và chúng sẽ **lệch nhau** trong vòng vài tháng. Lúc đó cùng một phép đo vào bằng hai cửa sẽ thành hai bản ghi, và yield tính ra sai — nhưng chỉ sai ở những dòng máy có cả hai loại thiết bị, nên rất khó tìm.

Một cửa vào, một định nghĩa danh tính. Adapter chỉ khác nhau ở phần **đọc**, không khác ở phần **định danh và ghi**.

---

### C16 — `feat(tools): add mqtt load harness for five thousand msg per second`

**Mục tiêu**: đo được N1 và N2 bằng số, không bằng cảm giác.

**Việc làm**
- Harness chạy **trong `ot-net`** (`scope.md` §13.1), bắn theo tốc độ đặt được.
- Metric `ingest.lag` = `recorded_at − device_timestamp`, xuất p50/p95/p99.
- `make load` với tham số msg/s và thời lượng.

**Kiểm chứng**: đây là **D2**.
```bash
make load RATE=5000 DURATION=600
```
Số vào `benchmarks.md`: throughput duy trì · p95 lag · CPU/RAM của ingestion · số message rơi ở EMQX (phải là 0).

> Đo lag bằng `recorded_at − device_timestamp` **chỉ đúng khi đồng hồ tốt**. Harness vì thế phải tắt fault lệch đồng hồ, và điều đó phải ghi ngay cạnh con số — nếu không, ai đó ở M8 sẽ đọc lại và tưởng lag từng âm.

---

### C17 — `test(ingestion): add one hour reconciliation with ten percent duplicates`

**Mục tiêu**: **D1** — mệnh đề quan trọng nhất của M2.

**Việc làm**
- Chạy simulator 1 giờ (nén thời gian **không** dùng ở đây — phải là 1 giờ tường thật, vì ta đang đo hành vi dưới tải liên tục).
- Vế trái: số phép đo **logic** simulator ghi ra file (C06).
- Vế phải: `SELECT count(*)` trên bảng telemetry.
- Hai số **bằng nhau**. Không "xấp xỉ", không "chênh dưới 0,1%".

**Kiểm chứng**
```bash
make reconcile
```
Ngoài số tổng, in thêm: số duplicate bị chặn · số row `Drifted` · số rebirth request · số publish hỏng. Bốn số đó là bối cảnh; nếu tổng khớp mà bốn số kia bằng 0 thì rất có thể fault chưa được bật, và phép kiểm chưa kiểm gì.

---

### C18 — `refactor(bus): point the bus lab at real consumers and drop the probe`

**Mục tiêu**: xoá scaffolding **mà không** xoá bằng chứng của M1 (§2.4).

**Việc làm**
- `bus-lab.sh` trỏ sang consumer thật của Ingestion thay vì `Nvm.BusProbe`.
- Chạy lại **cả ba** lệnh: `make bus-fanout`, `make bus-dlq`, `make bus-chaos`. Cả ba phải cho **cùng kết luận** như M1 (số cụ thể được phép khác — jitter và tải khác).
- **Rồi mới** xoá `src/Workers/Nvm.BusProbe/`.

**Kiểm chứng**
```bash
make bus-fanout && make bus-dlq && make bus-chaos
```
`make bus-dlq` vẫn phải assert **6 tên header `ce_*` duy nhất** và thoát 0 — đây là DoD D2 của M1, và nó phải sống sót qua M2.

> Thứ tự quan trọng: chuyển **trước**, xoá **sau**, trong cùng một commit nhưng không cùng một bước. Xoá trước rồi mới chuyển thì có một khoảng thời gian không ai chứng minh được M1 còn đúng — và nếu chuyển gặp trục trặc, khoảng đó dài ra vô hạn.

---

### C19 — `docs: close m2 with benchmarks and checklist`

**Mục tiêu**: đóng milestone bằng số.

**Việc làm**
- `docs/benchmarks.md` mục `## M2`, tối thiểu **14** dòng số thật: throughput · p95 lag · số message trên đĩa khi backend tắt 2 phút · thời gian tiêu backlog · số row lệch ở D1 · row thừa khi bỏ dedup · row bị nuốt khi bỏ `device_timestamp` khỏi khoá · kích thước buffer sau 30 phút · p95 lag khi xả không rate limit · thời gian tiêu backlog khi có rate limit · số vòng đỏ của `make buffer-crash` · số rebirth · số publish hỏng · thời gian `make ci`.
- `scope.md` §9/M2 sửa câu chữ D3 theo §2.3, và cập nhật Phụ lục A.
- `docs/oef-mapping.md`: các dòng M2 (*Edge connector*, *Data Collection*…) cập nhật trạng thái.
- `ADR-010` §Evidence điền con số của C13.3.
- Điền checklist §7.
- Đổi `status` của plan này sang `done` — **chỉ khi cả 5 DoD đạt**.

**Kiểm chứng**: đọc lại `benchmarks.md` tìm ô nào ghi ước lượng thay vì số đo. Có một ô như vậy thì M2 chưa đóng được (`AGENTS.md` §1.3).

---

## 6. Rủi ro riêng của M2

| # | Rủi ro | Dấu hiệu | Xử lý |
|---|---|---|---|
| R-M2-1 | "Duplicate" của simulator không phải duplicate thật | D1 xanh ngay lần đầu, counter dedup gần 0 | Test ở C06: bản trùng phải có **cùng** `source_event_id`. D1 xanh mà dedup counter = 0 là D1 **chưa kiểm gì** |
| R-M2-2 | Chạy simulator/gateway trên Windows cho tiện | Có dòng `dotnet run --project Nvm.Simulator` trong ghi chú, hoặc EMQX được thêm `ports:` lại | `scope.md` §13.1 và C07. Đây là hoàn nguyên R1 của M0 — thủng lại đúng lỗ đã bịt |
| R-M2-3 | Dedup không cùng transaction với insert telemetry | Hai `SaveChanges`, hoặc hai connection | C12. Khoá bền vững canh effect ở transaction khác = `ADR-022` lặp lại ở tầng khác |
| R-M2-4 | Đo lag bằng đồng hồ lệch | p95 lag âm, hoặc nhỏ bất thường | C16: harness tắt fault lệch đồng hồ, và ghi điều đó ngay cạnh số |
| R-M2-5 | Xoá `Nvm.BusProbe` làm mất DoD của M1 | `make bus-fanout` báo không tìm thấy project | §2.4 và C18: chuyển trước, xoá sau |
| R-M2-6 | Alias Sparkplug bị đoán bừa khi thiếu `NBIRTH` | Không có dấu hiệu — dữ liệu vào đúng hình dạng, sai ý nghĩa | C02: alias lạ **ném**. Đây là biến thể của lỗi *"không đọc được ≠ không có"* mà M1 đã gặp hai lần |
| R-M2-7 | Buffer đầy đĩa, gateway ghi đè bản cũ nhất | Không có dấu hiệu cho tới khi auditor tìm thấy lỗ hổng giữa chừng | C09: chạm ngưỡng thì **ngừng nhận, kêu to**. Ghi vào `ADR-028` |
| R-M2-8 | Hết 2,5 tuần vẫn chưa xong | Ngày thứ 10 mà mới tới C11 | Cắt theo thứ tự: CSV adapter (C15) → coating line và EOL tester trong simulator (giữ **một** loại máy) → gộp C11 vào M3. **Không cắt** C12, C17, C18 — đó là DoD và là bảo vệ DoD của M1 |

---

## 7. Checklist M2

Đánh dấu khi commit đã vào `main`.

| # | Commit | ☐ | Ngày | Ghi chú |
|---|---|---|---|---|
| C01 | sparkplug proto + protobuf toolchain | ☑ | 2026-08-28 | ADR-026 |
| C02 | decode sparkplug payload | ☑ | 2026-08-28 | |
| C03 | topic ↔ equipment path | ☑ | 2026-08-28 | |
| C04 | source event id từ natural key | ☑ | 2026-08-28 | Dùng lại `DeterministicGuid` của M1/C04 |
| C05 | formation cycler, nén thời gian | ☑ | 2026-08-28 | |
| C06 | fault: trùng, dropout, lệch đồng hồ | ☐ | | |
| C07 | simulator trong `ot-net` | ☐ | | `make net-check` phải vẫn 9/9 |
| C08 | gateway subscribe EMQX | ☐ | | ADR-027 |
| C09 | buffer store-and-forward | ☐ | | ADR-028 |
| C10 | rate limit + backpressure | ☐ | | ADR-029. **D3**. Lab #3 (xả 30 phút) |
| C11 | NBIRTH / NDEATH / seq gap | ☐ | | **D4** |
| C12 | processed_message + dedup | ☐ | | |
| C13 | clock quality classifier | ☐ | | **D5**. Lab #1 và #2 |
| C14 | publish cloudevents lên bus | ☐ | | |
| C15 | CSV file-drop adapter | ☐ | | |
| C16 | load harness 5.000 msg/s | ☐ | | **D2** |
| C17 | reconciliation 1 giờ | ☐ | | **★ D1** |
| C18 | chuyển bus lab, xoá probe | ☐ | | Bảo vệ DoD của M1 |
| C19 | benchmarks + đóng M2 | ☐ | | |

**Definition of Done**

| # | Tiêu chí | ☐ | Bằng chứng |
|---|---|---|---|
| ★ D1 | 1 giờ, 10% duplicate, số row khớp chính xác | ☐ | |
| D2 | ≥ 5.000 msg/s trong 10 phút, p95 lag < 5 s | ☐ | |
| D3 | Backend tắt 2 phút → 0 mất ở chặng thiết bị → ingestion, backlog < 3 phút | ☐ | |
| D4 | `NDEATH` → `STALE`, không xoá lịch sử | ☐ | |
| D5 | Lệch 2 giờ vẫn nhận, gắn `Drifted` | ☐ | |

**Sản phẩm phụ bắt buộc**

- [ ] `make ci` xanh, số test tăng thật (M1 kết thúc ở **328**)
- [ ] `make net-check` vẫn **9/9** sau khi thêm simulator, gateway, ingestion
- [ ] `ADR-026`, `ADR-027`, `ADR-028`, `ADR-029` viết xong — mỗi cái trong commit ra quyết định
- [ ] `ADR-010` §Evidence có con số bản ghi thừa của lab #1
- [x] `docs/glossary.md` có **backpressure**, **retry storm**, **rebirth**, **report-by-exception**, **`bdSeq`**, **poison message** *(làm trước, 2026-08-28)*
- [ ] `docs/benchmarks.md` có ≥ 10 dòng số thật cho M2, không ô nào là ước lượng
- [ ] `docs/event-catalog.md` cập nhật event M2
- [ ] `scope.md` §9/M2 sửa câu chữ D3 theo §2.3

> [!important] Câu hỏi "vì sao" cuối M2 (`AGENTS.md` §5.8.4)
> Trả lời **thành lời, không mở tài liệu**. Tắc câu nào thì phần đó chưa xong.
>
> 1. Vì sao dedup ở ingestion **không đủ**, và tầng thứ hai nằm ở đâu?
> 2. `NDEATH` cho biết điều gì mà "không nhận được message trong 5 phút" không cho biết?
> 3. Vì sao message lệch đồng hồ 2 giờ **vẫn được nhận**, thay vì bị từ chối?
> 4. Vì sao gateway phải **chậm lại** khi ingestion trả 503, thay vì thử lại nhanh hơn?
> 5. Vì sao `device_timestamp` phải nằm trong natural key, và chuyện gì xảy ra nếu bỏ nó ra?

---

## 8. Sau M2

M3 (*Telemetry, TimescaleDB & Production Calendar*) nhận đúng dòng dữ liệu M2 mở ra và bắt đầu **diễn giải** nó: hypertable, continuous aggregate, và `IProductionCalendar` — thứ trả lời *"phép đo lúc 23:47 thuộc ca nào, ngày sản xuất nào"*, kể cả ở `DE1` nơi có DST.

**Ba thứ M2 để lại cho M3 phải kiểm trước khi bắt đầu**:

1. **`clock_quality` có thật sự được điền không**, hay tất cả đều `Good` vì fault chưa bao giờ bật. M3 tính ca theo `device_timestamp`, nên một cột `Drifted` không ai điền sẽ làm ca bị tính sai âm thầm.
2. **Ba timestamp có đủ ba giá trị khác nhau trong DB không.** M3 sắp xếp theo `device_timestamp` khi tính nghiệp vụ và theo `recorded_at` khi audit (`scope.md` §7.3) — nếu M2 lỡ ghi đè một cột bằng cột khác thì hai phép sắp xếp đó thành một.
3. **Số row lệch ở D1 có đúng bằng 0 không**, hay đã bị làm tròn. Mọi con số của M3 tính trên tập dữ liệu này; sai số ở đây nhân lên ở đó.

Đọc `scope.md` §7.3 (ba loại timestamp), §2.3 (`DE1` và DST) và §9/M3 trước khi lập plan M3.
