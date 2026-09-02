# ADR-034 — Điều kiện nghiệm thu M3 phát biểu lại theo thứ đo được, không theo con số đã hứa

| | |
|---|---|
| **Status** | **Accepted** — owner phê duyệt **2026-08-31**, kèm **năm sửa đổi** (callout dưới) |
| **Date** | 2026-08-31 |
| **Liên quan** | ADR-011, ADR-030, ADR-031, ADR-032, `docs/scope.md` §4/§8.3/§8.4/§9 (M3 và M13), `docs/plans/M3-telemetry-timescaledb-production-calendar.md` §1/§2.4 |

> [!important] Bản đầu tiên tự đánh dấu `Accepted` **trước khi** owner duyệt
> Đó là một lỗi quy trình, không phải một chi tiết: cả bốn mệnh đề của ADR này đều nằm ở mức **CỨNG**
> theo `AGENTS.md` §2.1, và audit lúc đó đang nói rõ chúng *"chờ owner quyết"*.
> Một ADR tự duyệt chính thứ nó đang xin duyệt thì không còn là bằng chứng của một quyết định.
>
> Owner phê duyệt ngày **2026-08-31** với **năm sửa đổi**. Bản dưới đây là bản đã sửa; mỗi chỗ sửa
> mang dấu **(owner 2026-08-31)** ngay tại chỗ:
>
> 1. **D1** — trục dung lượng phải đo **tại cùng một cardinality**, và **cùng điều kiện dữ liệu**.
> 2. **D1** — câu hỏi sức chứa 24 giờ **không biến mất**: nó thành **hard capacity/soak test ở M13**,
>    và N1/N2 giữ nguyên qualification **M9** / requalification **M13**.
> 3. **D2** — số của cả line là **evidence có deadline**, không phải một dòng ghi rồi thôi.
> 4. **D3** — luật từ M4 là **tái lập RED trên parent SHA bằng diff chỉ-chứa-test**, **không** đòi một
>    commit đỏ nằm trên `main`. Sai lệch của riêng M3 được owner chấp nhận, không rewrite history.
> 5. **O8/K13** — giả định *"recreate container là xoay được mọi credential"* của runbook cũ
>    **không được dùng**; chỉ [`docs/runbook.md`](../runbook.md) §1 là hướng dẫn hiện hành.
>
> [!warning] Đính chính 2026-09-02 — teach-back chưa được miễn
> Câu *"bỏ qua các câu hỏi"* đã bị diễn giải quá phạm vi thành quyền thay một hard DoD. Nó chỉ đủ để
> dừng hỏi trong lượt đó, không đủ để đánh dấu phép đo là đạt hoặc xoá nó khỏi điều kiện đóng. M3 vì
> vậy vẫn mở; teach-back và dòng Data Collection trong `oef-mapping.md` đều chưa hoàn tất.

---

## Context

Hai vòng audit M3 dừng ở cùng một chỗ: **ba mệnh đề DoD không thể đóng bằng code, chỉ đóng được
bằng một quyết định của chủ dự án.** Audit đúng khi từ chối tự quyết — đổi điều kiện nghiệm thu để
làm cho nó đạt là cách hỏng kinh điển nhất của mọi bảng DoD.

Ba mệnh đề đó:

| | `scope.md`/plan hứa gì | Đo được gì | Vì sao lệch |
|---|---|---|---|
| **D1** | *"24 giờ telemetry (≈ **86 triệu điểm**) nén xuống < 15%"* | 8,385723 % và 8,140036 % ở hai cardinality, lệch **0,245687 pp**; dataset lớn nhất **2,19 triệu row** | 86 triệu điểm qua đường thật là **172 triệu row** (mỗi row telemetry bắt buộc có một claim, khoá ngoại) và **hơn 6 giờ** chạy liên tục cho **mỗi lần** đo |
| **D2** | *"nhiệt độ trung bình mỗi phút của **`FORM-01`** trong 7 ngày < 200 ms"* | một kênh **29,627 ms**; toàn bộ `FORM-01` **144,061 ms** | Plan viết nhầm `FORM-01` có 1.000 kênh. Catalog thật: `F1` là **line**, `FORM-01` là **một cycler 100 kênh**, line có 10 cycler |
| **D3** | *"cả hai test DST **đỏ trước, xanh sau**"* | 8/8 xanh; mutation cho thấy 8/564 đỏ khi cài đặt bị phá | C02 đã cài đặt đúng **trước** khi C03 viết test. Lịch sử commit không thể sửa lại |

Đây là **learning project**, và mục tiêu học là *nghiệp vụ ngành pin* + *cách viết .NET sát
production*. Điều đó quyết định tiêu chuẩn dùng ở đây: không phải "làm sao cho DoD xanh", mà
**"phát biểu lại DoD sao cho nó nói đúng thứ nó muốn bảo vệ, và nói bằng thứ đo được"** — đúng việc
một kỹ sư phải làm khi một NFR viết trên giấy gặp một hệ thống thật.

File audit tạm giữ danh sách trong lúc chờ quyết định; ADR này làm các quyết định đó sống lâu sau khi
file tạm được xoá lúc đóng M3.

## Decision

### D1 — điều kiện là **tỉ số ổn định**, không phải số dòng

Bỏ ngưỡng *"≈ 86 triệu điểm"*. D1 đạt khi **cả năm** mệnh đề đúng:

1. Tỉ số nén **< 15 %** ở **mọi** phép đo; **và**
2. đo ở **hai cardinality** khác nhau, cùng số ngày, hai tỉ số lệch **< 2 điểm phần trăm**; **và**
3. đo ở **hai bậc dung lượng** khác nhau **tại cùng một cardinality** — bậc lớn **≥ 4×** bậc nhỏ về
   số row — hai tỉ số cũng lệch **< 2 điểm phần trăm** *(owner 2026-08-31: mệnh đề "tại cùng
   cardinality" là phần bản đầu bỏ sót, và bỏ nó thì điều kiện (3) rỗng — xem ngay dưới)*; **và**
4. **mọi** cặp tỉ số trong toàn bộ các phép đo lệch **< 2 điểm phần trăm** — tức `max − min` của cả
   tập, không chỉ lệch trong từng cặp; **và**
5. cả bốn phép đo dùng **đúng code đường cong của simulator** và **cùng điều kiện dữ liệu**: cùng chu
   kỳ mẫu, cùng tỉ lệ lệch đồng hồ, cùng site, cùng `segmentby`, cùng đường ghi `COPY`. Không
   `random()`, không hằng số *(owner 2026-08-31 nâng "cùng code đường cong" thành "cùng điều kiện
   dữ liệu" — hai lượt sinh cùng code nhưng khác chu kỳ mẫu là hai bộ dữ liệu khác nhau)*.

Điều kiện (3) là **thứ vòng audit đầu tiên làm rơi mất**. Phát biểu lại của plan §2.4 lập luận đúng
rằng tỉ số nén là tính chất của **hình dạng** dữ liệu chứ không phải của số dòng — rồi chỉ đo trên
trục cardinality và **không đo trục dung lượng**, tức là bỏ qua đúng cái trục mà `scope.md` nói tới.
Một lập luận đúng vẫn cần bằng chứng trên trục mà nó đang bác bỏ.

**Vì sao (3) phải ghim cardinality.** Bản đầu chỉ nói *"hai bậc dung lượng ≥ 4×"*, và bộ dữ liệu đã có
thoả điều đó một cách **vô nghĩa**: `channels_8` là **437.798** row, `channels_40` là **2.186.459**
row — đúng **4,99×**. Nhận cặp đó là để điều kiện (3) **không đo gì mới**, vì nó chỉ là điều kiện (2)
viết lại bằng đơn vị khác: hai "bậc dung lượng" ấy khác nhau **chỉ vì** cardinality khác nhau. Ghim
cardinality tách hai trục thành hai câu hỏi thật sự khác nhau — (2) đổi **hình dạng** dữ liệu, (3)
đổi **số dòng** trên cùng một hình dạng.

**Phép đo đã chốt** *(owner 2026-08-31)*: **40 kênh × 7 ngày** so với **40 kênh × 28 ngày** — đúng
**4×** số ngày, cùng cardinality, cùng chu kỳ 5 giây. Cửa sổ phải **không trùng** hai fixture đã pin
của C09 lẫn fixture 7 ngày 100 kênh của C10: `scripts/sql/compression-report.sql` kiểm fixture C09
bằng fingerprint và `RAISE` khi lệch, nên chạm vào chúng làm hỏng D1 và D2 **cùng lúc**.

**Preflight bắt buộc trước khi sinh dữ liệu** — `R-M3-7`, và `benchmarks.md` luật #1 cấm ước lượng:

| Kiểm | Vì sao |
|---|---|
| Dung lượng trống của volume Postgres, đo **trước** và ghi lại | 35 ngày × 40 kênh × 6 signal ÷ 5 giây ≈ **14,5 triệu** row telemetry, **và** chừng ấy row claim trong `ingest.processed_message` (khoá ngoại) |
| Cửa sổ ngày còn trống, không đụng fixture C09/C10 | Xem trên |
| Thời gian sinh mỗi triệu row, ngoại suy từ số C08 đã ghi | Nếu lượt 28 ngày không xong trong ngân sách thì **đổi cặp** (ví dụ 5 ngày so với 20 ngày), giữ đúng tỉ lệ ≥ 4× |

Preflight trượt thì **đổi cặp, không đổi điều kiện**.

> [!important] Đoán trước khi đo — `AGENTS.md` §5.8.4 *(owner 2026-08-31)*
> Owner **dự đoán hai tỉ số nén và độ lệch giữa chúng trước khi lệnh đo chạy**; dự đoán được ghi vào
> `benchmarks.md` **cạnh** số đo. Agent **không** được đo trước rồi hỏi sau — làm vậy thì "dự đoán"
> chỉ còn là một câu viết lại kết quả, đúng thứ `O7` dưới đây từ chối hồi tố cho C09/C10.

**Ranh giới** *(owner 2026-08-31 siết lại)*: ADR này **không** nói 86 triệu điểm là vô nghĩa, và
**không** cho phép câu hỏi đó bốc hơi. Nó chuyển câu hỏi sang chỗ đúng của nó — và chỗ đó nhận một
**điều kiện cứng**, không nhận một lời hứa:

| Câu hỏi | Ở đâu | Điều kiện |
|---|---|---|
| Hệ thống **chứa và tiêu hoá được bao nhiêu** dữ liệu trong 24 giờ | **M13** | **Hard capacity/soak test tương đương 24 giờ** telemetry liên tục ở quy mô mà `scope.md` §9/M3 gọi là *"≈ 86 triệu điểm"*. Là **DoD của M13**, không phải mục tuỳ chọn — viết vào `scope.md` §9/M13 **ngay trong lượt này**, không để dành |
| Đường ống **nhanh bao nhiêu**: N1 ≥ 5.000 msg/s, N2 p95 < 5 s | **qualification ở M9** | `ADR-031`, **không đổi** |
| Ba thứ M13 thêm vào đường nóng có làm mất số đó không | **requalification ở M13** | `ADR-031`, **không đổi** |

Ba dòng này là **ba câu hỏi khác nhau**, và gộp chúng là cách chúng cùng biến mất: sức chứa hỏi *"dữ
liệu 24 giờ có nằm vừa và còn đọc được không"*, N1 hỏi *"nuốt được bao nhiêu mỗi giây"*, N2 hỏi *"mất
bao lâu từ thiết bị tới DB"*. Một hệ thống đạt N1 trong 10 phút vẫn có thể chết ở giờ thứ chín vì
autovacuum không theo kịp — đó là thứ chỉ soak test 24 giờ nhìn thấy.

> [!warning] Audit 2026-09-02 — soak 24 giờ tự nó không tạo được dữ liệu đủ lạnh
> Một lần chạy bắt đầu từ DB trống chỉ sinh **hot head**; sau 24 giờ không row nào cũ hơn ngưỡng nén
> 7 ngày. Vì vậy M13 phải pre-seed **cold tail** trên cùng topology trước `T0`, refresh parent rồi child,
> và ghi baseline trạng thái chunk/job trước khi chạy hot head 24 giờ. Oracle sau chạy phải thấy cả ba
> tầng raw/parent/child có cold chunk đã nén, hot chunk còn rowstore và compression job thật sự tiến
> lên. D2 phải được đo ở cửa sổ cold-only, hot-only và cửa sổ đi qua cả hai; chỉ ghi "đã chạy soak"
> hoặc chỉ đo fixture nén hoàn toàn không hoàn thành nghĩa vụ này. Contract executable nằm tại
> `scope.md` §9/M13.

Nếu M13 lại phát biểu lại một trong ba dòng trên thì dự án **không bao giờ** trả lời câu hỏi sức chứa,
và ADR này trở thành cái cớ đã dùng để hoãn nó.

### D2 — `FORM-01` nghĩa là **một cycler 100 kênh**, và ngưỡng áp cho nó

`FORM-01` trong `deploy/seed/factory-model.r3.json` là **một máy**, có 100 kênh. Đường dẫn thiết bị
là định danh, không phải cách nói xấp xỉ. **D2 đạt ở 144,061 ms.**

Kèm một nghĩa vụ **không phải gate M3**: `benchmarks.md` phải ghi thêm **số của cả line `F1` —
10 cycler × 100 kênh = **1.000 kênh** — cho cùng truy vấn *(owner 2026-08-31 sửa số học: bản đầu viết
"10 cycler × 1.000 kênh", tức 10.000 kênh, và đó là một topology không tồn tại)*.

Ba ràng buộc của phép đo đó *(owner 2026-08-31)*:

1. **Bằng topology thật**, không bằng phép nhân trên giấy: `deploy/seed-load/` là seed 10 cycler và là
   thứ `make load` đã dùng ở M2/D2. Ngoại suy 144 ms × 10 **không** phải một con số đo được.
2. **Là evidence, không phải gate M3.** D2 **đạt ở 144,061 ms** trên `FORM-01`. Số của line không làm
   M3 trượt dù nó bao nhiêu.
3. **Nếu ≥ 200 ms thì ghi nợ kèm deadline**, và deadline là **trước dashboard line-wide của M6/M7** —
   milestone đầu tiên có người thật mở một màn hình gộp cả line. Nợ không có deadline là nợ đã quên;
   nợ có deadline nằm sau chỗ nó phát nổ thì cũng vậy.

Lý do nghĩa vụ này tồn tại: đó là truy vấn mà một kỹ sư quy trình sẽ chạy **thật** ở M6/M7, và con số
đó phải **tồn tại trước** khi có ai dựa vào nó — không phải được phát hiện lúc dashboard chậm.

**Không** nới D2 lên mức line. Siết một DoD sau khi đã đo là cách khác của việc sửa DoD cho khớp kết
quả — chỉ theo chiều ngược lại, và cũng sai như thế.

### D3 — hành vi **đạt**, quy trình **không đạt**, và ghi cả hai

**D3 đạt về hành vi.** Test phân biệt được đúng/sai — mutation cho 8/564 đỏ khi cài đặt bị phá, tức
là test **không tautology**. Đó là tính chất mà *"đỏ trước xanh sau"* tồn tại để bảo đảm.

**D3 không đạt về quy trình, và repo ghi đúng như vậy.** C02 đã có cài đặt trước C03. Không diễn giải
lại chữ *"đỏ trước"* cho vừa với việc đã làm; không sửa lịch sử commit.

**Mutation KHÔNG thay thế TDD history** *(owner 2026-08-31, phát biểu dứt khoát)*. Hai thứ chứng minh
hai điều khác nhau và không đổi cho nhau được:

| | Chứng minh điều gì | Không chứng minh điều gì |
|---|---|---|
| **Mutation** (8/564 đỏ) | Test **phân biệt được** cài đặt đúng với cài đặt sai | Test được viết khi tác giả **chưa biết đáp án** |
| **TDD history** (đỏ trước) | Test viết ra từ **mệnh đề nghiệp vụ**, không từ cài đặt đang có | Test đủ nhạy — một test đỏ-rồi-xanh vẫn có thể là tautology |

**Owner chấp nhận sai lệch này cho riêng M3**, và **không** rewrite history. Chấp nhận ở đây nghĩa là:
riêng D3 không còn là blocker nếu được ghi *"hành vi đạt, quy trình không đạt"*, và dòng chữ đó
**ở lại** trong hồ sơ M3. Quyết định này không thay teach-back hay blocker khác, và **không** tạo tiền
lệ cho M4 trở đi.

**Luật từ M4 trở đi** *(owner 2026-08-31 phát biểu lại — bản đầu đòi commit đỏ trên `main`, và điều đó
vừa quá mạnh vừa đo sai thứ)*:

> Khi DoD nói *"đỏ trước"*, điều kiện là **RED tái lập được trên parent SHA bằng một diff chỉ chứa
> test**, và nó phải đỏ **bởi đúng assertion nghiệp vụ** mà DoD nói tới.

Bốn vế, mỗi vế loại một cách lách:

| Vế | Loại cách lách nào |
|---|---|
| **trên parent SHA** | Chạy test mới trên commit **trước** commit cài đặt — không phải trên HEAD, nơi cài đặt đã có sẵn |
| **diff chỉ chứa test** | Không kèm một mẩu production code nào; nếu phải sửa production code để test biên dịch được thì phép kiểm này **chưa hợp lệ** và phải nói ra |
| **đỏ bởi đúng assertion nghiệp vụ** | Đỏ vì `CS0246 không tìm thấy kiểu` hoặc `NotImplementedException` **không tính**. Phải đọc được dòng `Assert` nào đỏ, và nó phải là dòng nói về ca DST / `production_day` / hành vi đang xét |
| **tái lập được** | Ghi đúng lệnh và đúng SHA vào commit body hoặc plan, để người khác chạy lại ra cùng kết quả |

**Không** yêu cầu một commit đỏ nằm trên `main`. Lý do owner bỏ vế đó: một commit đỏ trên nhánh chính
làm `git bisect` và mọi phép kiểm CI trên history mất nghĩa, và nó **không** thêm bằng chứng nào so
với phép tái lập trên parent SHA — nó chỉ đóng băng bằng chứng ấy ở một chỗ bất tiện.

M3 đã có sẵn **một ví dụ làm đúng** để so: bản sửa lịch sản xuất sau audit (`M3/C15`) hoàn nguyên cài
đặt, **nhìn 2 test đỏ**, rồi mới khôi phục — đúng hình dạng luật M4, chỉ khác là nó hoàn nguyên trên
working tree thay vì checkout parent SHA. Hai ví dụ nằm cạnh nhau trong cùng một milestone đáng giá
hơn một milestone toàn ví dụ đúng.

### O4 — **giữ** index composite, và giữ vì đã đo

`ix_telemetry_measurement_site_device_time (site_id, equipment_id, device_timestamp DESC)` **được
giữ**. Migration `003` hứa *"C10 đo xem nó có thực sự được dùng không và bỏ ở đó nếu `EXPLAIN` nói
ngược lại"*; vòng audit thứ hai chỉ ra phép đo đã làm chạy trên **chunk đã nén**, nơi truy vấn dùng
index columnstore do `segmentby` — tức là chưa đo cái đang hỏi.

Đo lại trên **chunk rowstore** (9 chunk chưa nén, 518.794 row, cửa sổ 7 ngày, truy vấn mức máy):

| | Kế hoạch | Lần chạy nguội | Lần chạy nóng |
|---|---|---:|---:|
| Có index | `Bitmap Index Scan` trên `ix_..._site_device_time` | **106,989 ms** | **77,283 ms** |
| Ép bỏ index | `Seq Scan` từng chunk | **337,010 ms** | **130,020 ms** |

Kết luận: index rút ~**3,15×** khi nguội, ~**1,7×** khi nóng — và **337 ms vượt ngân sách 200 ms của
D2**. Không có nó, D2 trượt trên chính đường dữ liệu chưa nén, tức là dữ liệu **mới nhất**, tức là dữ
liệu người ta điều tra nhiều nhất.

### O8 / K13 — repo **ngừng xuất bản** credential dùng được; xoay giá trị local là việc của chủ máy

Hai việc tách rời, và trộn chúng là lý do mục này bị đánh dấu xong nhầm một lần:

1. **Việc của repo (làm trong commit này).** `.env.example` không còn chứa mật khẩu chạy được — chỉ
   còn placeholder. Một bản clone mới **không thể** khởi động bằng credential đã công bố.
2. **Việc của chủ máy (không làm hộ).** `.env` trên máy này vẫn giữ 9 giá trị cũ. Xoay chúng chạm vào
   stack đang chạy và có thể làm hỏng nó. Owner đã chọn đường: **xoay tại chỗ, giữ toàn bộ volume và
   dataset.**

**Ràng buộc owner đặt cho việc xoay** *(owner 2026-08-31)* — cả năm đều là mức **CỨNG**:

| # | Ràng buộc | Vì sao |
|---|---|---|
| 1 | **Xoay tại chỗ cả 9 credential**, giữ nguyên mọi volume và dataset | Dataset 2,19 triệu row là nền của bằng chứng D1/D2/D5; và **không ai wipe production để đổi mật khẩu** |
| 2 | **Cấm `make down-v`** | Xoá volume là mất dataset **và** mất object `raw-curve` đang nằm dưới object lock COMPLIANCE |
| 3 | **Không dùng giả định recreate container của runbook cũ** | Nó gộp `MinIO / EMQX / Keycloak / Grafana` thành nhóm *"đọc credential từ env lúc khởi động"*. Chỉ 2/4 đúng; dùng giả định đó có thể làm `.env` đổi nhưng service vẫn giữ mật khẩu cũ |
| 4 | **Kiểm cơ chế thật của từng service, đúng version đang chạy**, trước khi chạm vào nó | Postgres `nvm`/`nvm_grafana`, MSSQL `sa`/`nvm_app`, RabbitMQ, EMQX Dashboard, MinIO, Keycloak, Grafana admin — bảy hệ, và chúng **không** cùng một cơ chế |
| 5 | **Không in secret ra output**: không `echo` giá trị, không đọc `.env` ra màn hình, không `docker compose config`, không `docker inspect` phần environment | Cả hai lệnh cuối in **toàn bộ** biến môi trường đã resolve. Audit M3 đã rò rỉ credential **hai lần** theo đúng đường đó (`docs/audit-playbook.md` §3) |

Owner nhập giá trị mới **ngoài chat**. Agent viết các bước, không cầm secret.

Runbook đã kiểm cơ chế nằm ở **[`docs/runbook.md`](../runbook.md) §1**. Nó bác bỏ giả định của bản
runbook cũ bằng số đo trên chính stack này: trong bốn service bị gộp thành nhóm *"đọc env lúc khởi
động"*, chỉ **MinIO** và **Keycloak** đúng; **EMQX** giữ dashboard user trong
mnesia (`emqx_admin`) với node name đã ghim, **Grafana** giữ admin trong `grafana.db` nằm ở volume.
Runbook cũ còn bỏ sót `nvm_app` của MSSQL. Cả bốn kết luận đó nay nằm trong runbook hiện hành.

Vì mọi giá trị cũ **đã nằm trong lịch sử git** và không rút lại được, `scripts/burned-credentials.sha256`
giữ **hash SHA-256** của chúng và `make secret-check` từ chối một `.env` còn dùng bất kỳ giá trị nào
trong đó. Đổi `.env.example` sang placeholder mà không có danh sách này sẽ làm phép kiểm **xanh giả**:
giá trị cũ không còn khớp file example nữa, dù nó vẫn công khai như cũ.

Audit 2026-09-02 siết tiếp provenance của bằng chứng runtime: `rotate-verify.sh` chỉ được chạy sau khi
backup có đủ chín key và **hash của từng old value khớp đúng key** trong manifest trên. `old != new`
không đủ, vì một backup synthetic vẫn làm chín phép *old must fail* xanh. Matrix preflight hiện
**45/45**; backup lịch sử thật khớp **9/9**. `rotate-credentials.sh` còn kiểm node RabbitMQ và đổi nó
đầu tiên trước khi mutate service khác; đường thủ công trong runbook buộc đủ **9 new + K3 + 9 old**.

## Consequences

**Được**

- Ba mệnh đề DoD nói đúng thứ chúng muốn bảo vệ, và mỗi mệnh đề có một phép đo **chạy lại được**.
- D1 mạnh hơn bản gốc ở đúng chỗ quan trọng: bản gốc kiểm **một** điểm dữ liệu lớn, bản này kiểm
  **tính ổn định trên hai trục**. Một tỉ số 14,9 % ở 86 triệu row không nói gì về việc nó có ổn định
  hay không; bốn phép đo trên hai trục thì có.
- Quyết định giữ index có số đứng sau, và số đó nói rõ mất index thì **D2 trượt**.
- K13 tách được "repo có xuất bản secret không" (đóng được bằng commit) khỏi "máy này có đang dùng
  secret đã lộ không" (chỉ đóng được bằng người vận hành). Trước đây hai câu này bị gộp và bị đánh
  dấu xong khi mới xong một nửa.

**Mất / phải chịu**

- **M3 chưa đóng được ngay sau ADR này.** D1 điều kiện (3) đòi **hai lượt backfill mới ở cùng
  cardinality** (40 kênh × 7 ngày và 40 kênh × 28 ngày) chưa chạy — nhiều việc hơn bản đầu, vì bản đầu
  nhận cặp `channels_8`/`channels_40` sẵn có làm "hai bậc dung lượng". ADR chỉ dời được ranh giới,
  không thay được phép đo.
- Ghim cardinality ở điều kiện (3) làm D1 **đắt thêm khoảng 14,5 triệu row** dữ liệu sinh mới, cộng
  chừng ấy row claim. Đó là cái giá của việc điều kiện (3) thật sự đo một trục thứ hai.
- Bỏ ngưỡng 86 triệu điểm nghĩa là M3 **không** còn nói gì về việc hệ thống chịu được bao nhiêu dữ
  liệu. Câu đó chuyển hẳn sang **hard capacity/soak test 24 giờ ở M13** (`scope.md` §9/M13, thêm
  trong lượt này) và **phải** được hỏi ở đó — nếu M13 cũng phát biểu lại nó lần nữa thì dự án không
  bao giờ trả lời câu hỏi sức chứa. N1/N2 giữ nguyên qualification **M9** / requalification **M13**
  (`ADR-031`): sức chứa và thông lượng là hai câu hỏi, và M13 phải trả lời cả hai.
- Ghi D3 là *"quy trình không đạt"* để lại một vết đỏ vĩnh viễn trong hồ sơ M3. Đó là chủ ý: một
  milestone toàn màu xanh mà có một mục được diễn giải cho vừa thì mọi màu xanh khác cũng mất giá.
- Index composite trả giá bằng **ghi**: mỗi insert phải bảo trì thêm một btree trên
  `(site_id, equipment_id, device_timestamp)`. `ADR-030` đã ghi nợ đường cong suy giảm insert; index
  này nằm trong khoản nợ đó và **M9** phải đo nó, không được quên vì hôm nay nó có lợi cho đọc.
- Placeholder trong `.env.example` làm bản clone mới **không chạy ngay được** — thêm một bước cho
  người mới. Đây là đánh đổi có chủ ý và là cách các repo thật làm.

## Alternatives considered

| Phương án | Vì sao không chọn |
|---|---|
| **D1**: sinh đủ 86 triệu điểm rồi đo một lần | Mỗi lần đo lại tốn > 6 giờ và ~172 triệu row; và một tỉ số tại **một** kích thước vẫn không chứng minh được tính ổn định — chính là thứ mệnh đề muốn nói |
| **D1**: giữ nguyên chữ trong `scope.md`, đánh dấu D1 là "trượt" rồi vẫn đóng M3 | Đóng milestone với một DoD trượt là dạy chính xác thói quen mà bảng DoD tồn tại để chống |
| **D2**: nới ngưỡng lên mức line 1.000 kênh | Sửa DoD **sau khi** đã biết kết quả, kể cả theo chiều làm khó hơn, vẫn là sửa DoD cho khớp kết quả. Số của line vẫn được ghi, chỉ không phải là gate |
| **D3**: viết lại lịch sử commit cho C02/C03 để có "đỏ trước" | Làm giả bằng chứng. Giá trị duy nhất của lịch sử là nó không sửa được |
| **D3**: coi mutation-red **tương đương** TDD history | Không tương đương. Mutation chứng minh test **phân biệt được**; TDD history chứng minh test được viết khi **chưa biết đáp án**. Cái sau mạnh hơn về quy trình, cái trước mạnh hơn về chất lượng test — ghi cả hai, đừng đánh tráo |
| **O4**: bỏ index cho nhẹ đường ghi | Đo rồi: bỏ thì truy vấn mức máy lên **337 ms** trên chunk nguội, tức D2 trượt |
| **O8**: tự xoay 9 credential trên stack đang chạy | Chạm vào hệ thống đang chạy của người khác, và người đặt secret phải là người giữ secret (`AGENTS.md` §3.6) |
| **O8**: `make down-v && make up` cho sạch | Owner cấm. Xoá volume là mất dataset 2,19 triệu row **và** object `raw-curve` dưới object lock COMPLIANCE. Đổi mật khẩu bằng cách xoá dữ liệu không phải một thao tác vận hành, nó là một sự cố |
| **D1**: nhận cặp `channels_8` / `channels_40` (4,99×) làm hai bậc dung lượng | Hai bậc đó khác nhau **chỉ vì** cardinality khác nhau, nên điều kiện (3) sẽ chỉ là điều kiện (2) viết lại — xem §D1 |
| **D3**: đòi một commit đỏ nằm trên `main` | Làm `git bisect` và mọi phép kiểm CI chạy trên history mất nghĩa, mà **không** thêm bằng chứng nào so với tái lập trên parent SHA |

## Evidence tái lập

```bash
# O4 — index composite trên chunk rowstore
docker exec nvm-timescale psql -U "$NVM_POSTGRES_USER" -d "$NVM_POSTGRES_DB" -f /tmp/idx4.sql
```

Chạy 2026-08-31 trên `timescale/timescaledb:2.29.2-pg17`, 61 chunk (52 nén / 9 rowstore),
518.794 row trong cửa sổ `[2026-08-24, 2026-08-31)`:

- có index → `Bitmap Index Scan on _hyper_1_N_chunk_ix_telemetry_measurement_site_device_time`,
  **106,989 ms** nguội / **77,283 ms** nóng;
- `SET enable_bitmapscan=off; enable_indexscan=off;` → `Seq Scan` mọi chunk,
  **337,010 ms** nguội / **130,020 ms** nóng.

### Kết quả kỹ thuật C15 — milestone vẫn mở

| # | Việc | Trạng thái | Bằng chứng |
|---|---|---|---|
| 1 | **Preflight D1**: dung lượng trống, cửa sổ ngày còn trống, ngoại suy thời gian sinh | ☑ | đĩa trống 980,0 GB; cửa sổ fixture trống |
| 2 | **Owner dự đoán** hai tỉ số và độ lệch, ghi trước phép đo | ☑ | ghi trong `M3/C15`, dự đoán lệch < 0,5 pp |
| 3 | **Backfill 40 kênh × 7 ngày** và **40 kênh × 28 ngày**, gate 4 scenario | ☑ | 8,147113 % / 8,151416 %, lệch 0,004303 pp |
| 4 | **`rollup-bench` mức line và mức máy** bằng topology thật | ☑ | gate mức máy p95 8,080 ms; line ấm khoảng 20 ms |
| 5 | **Xoay 9 credential tại chỗ** theo `docs/runbook.md` §1 | ☑ | 19/19 phép, 9/9 giá trị cũ bị từ chối |
| 6 | **Teach-back** 3 câu ở plan M3 §7 | ☐ | Chưa thực hiện; đây vẫn là hard DoD và dòng OEF giữ `đang làm` |

Bước (2) đã đứng **trước** (3): đo trước rồi hỏi dự đoán sau thì dự đoán không còn là dự đoán.
