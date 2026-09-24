# AGENTS.md — Nguyên tắc làm việc trong repo này

> Dành cho mọi AI agent (Claude Code, Copilot, Cursor…) và cả người làm việc trong repo `novavolt-mes`.
>
> Tài liệu này **đứng trên** `docs/scope.md` và mọi plan. Khi có mâu thuẫn, theo tài liệu này.

---

## 0. Bối cảnh một dòng

Đây là dự án MES giả lập để hoàn thiện đầy đủ chức năng và kiểm chứng kỹ thuật trước khi ánh xạ sang Opcenter và quy ước công ty. Kiến thức của chủ repo là mục tiêu học tập riêng, **không phải điều kiện nghiệm thu hoặc điều kiện dừng triển khai** (quyết định 2026-09-23, [ADR-039](docs/adr/ADR-039-delivery-without-learning-gates.md)).

### 0.1 Trách nhiệm triển khai của agent

Chủ repo không tự gõ phần lớn code .NET. Agent chịu trách nhiệm triển khai, sửa finding và thu thập bằng chứng cho toàn bộ M0–M13. Tiếp tục qua các đơn vị việc và milestone đã nằm trong scope, không chờ chủ repo commit, trả lời teach-back hoặc dự đoán số đo. Chỉ dừng ở việc thực sự cần quyền truy cập, thao tác UI không có công cụ hỗ trợ, hoặc quyết định đổi contract cứng.

Nền tảng chuyên môn của chủ repo, để agent nhắm đúng chỗ:

| Mảng | Mức | Nghĩa là |
|---|---|---|
| .NET, C#, SQL, system design, distributed systems | **vững** | Không giải thích `record`, DI, index, CQRS là gì. Giải thích **vì sao chọn cách này ở đây** |
| **Nghiệp vụ sản xuất pin / MES / traceability** | **mới** | Giải thích thuật ngữ ngắn gọn khi báo cáo nếu cần để người đọc hiểu kết quả |
| Opcenter EF, Mendix | đang học | Giữ ánh xạ và bằng chứng riêng; không lấy mức hiểu của người học làm gate |

Quy tắc báo cáo ở **§5.8**. Nội dung dạy học có thể thực hiện khi được yêu cầu, độc lập với trạng thái chức năng.

---

### 0.2 Cách thực hiện được chủ repo chốt 2026-09-23

Agent nhận toàn bộ phần việc còn lại. Không phải tạo plan riêng cho từng milestone, không chia tiến độ thành lượt chờ theo từng commit. Dùng scope/DoD và plan hiện có làm tham chiếu; giữ tracker ngắn, triển khai và kiểm chứng liên tục. Chỉ nhờ chủ repo thực hiện thao tác Mendix không có công cụ hỗ trợ, hoặc cung cấp quyền truy cập/quyết định contract cứng thật sự thiếu. Agent được tự commit/push theo §1.1.

## 1. Quy tắc tuyệt đối — không có ngoại lệ

### 1.1 Agent được tự commit và push

Chủ repo giao toàn bộ việc triển khai và cho phép agent tự commit/push các thay đổi thuộc dự án (2026-09-23). Không cần xin xác nhận theo từng commit. Trước khi commit, kiểm diff, loại secret/artifact tạm, xác nhận các kiểm tra phù hợp đã đạt; trước khi push, xác nhận đúng repository, branch và remote. Giữ các commit có phạm vi rõ; không trộn thay đổi ngoài nhiệm vụ. Không force-push hoặc bỏ thay đổi của người khác khi chưa có chỉ dẫn riêng.

### 1.2 Không phá working copy của người khác

- Không `git reset --hard`, `git checkout -- .`, `git stash drop`, `git clean -fd`.
- Không đổi branch khi working copy đang dirty.
- Với Mendix: **không bao giờ** đề xuất "Revert All Changes" như một cách xử lý tiện.

### 1.3 Báo cáo trung thực

- Test đỏ thì nói đỏ, kèm output. Không "về cơ bản là xong".
- Bỏ qua một bước thì nói rõ bỏ bước nào và vì sao.
- Không đoán kết quả benchmark. Chưa đo thì ghi `chưa đo`, không điền số ước lượng.

---

## 2. Quan hệ với docs và plan — được phép làm trái

`docs/scope.md` và các plan trong `docs/plans/` là **bản đồ, không phải đường ray**. Chúng được viết trước khi gõ dòng code đầu tiên, nên chúng sẽ sai ở đâu đó. Đó là điều bình thường và được dự liệu.

### 2.1 Ba mức ràng buộc

| Mức | Nội dung | Được lệch không? |
|---|---|---|
| **CỨNG** | Quy tắc ở §1; Definition of Done đo được bằng số; ràng buộc an toàn ở §4 | **Không.** Muốn đổi → phải hỏi và được đồng ý trước |
| **MỀM** | Kiến trúc, thư viện, thứ tự công việc, cách chia file, tên biến, chi tiết schema | **Có**, theo quy trình §2.3 |
| **GỢI Ý** | Version image cụ thể, tên thư mục, format commit message, ví dụ code trong docs | **Có**, tự quyết, chỉ cần nói một câu |

### 2.2 Khi nào NÊN làm trái docs

Chủ động đề xuất lệch khi gặp một trong các dấu hiệu sau:

- **Docs mâu thuẫn với thực tế kỹ thuật.** Ví dụ: thư viện được nêu đã ngừng hỗ trợ, image tag không còn tồn tại, API đã đổi.
- **Có cách đơn giản hơn đạt cùng DoD.** Docs đề xuất closure table; nếu một index phù hợp đã đạt p95 < 200 ms trên tập dữ liệu thật thì nói ra, đừng làm closure table vì "docs bảo thế".
- **Docs bỏ sót một trường hợp thật.** Phát hiện ra một luồng nghiệp vụ mà scope chưa nghĩ tới.
- **Thứ tự công việc không tối ưu.** Làm B trước A tiết kiệm được một lần rework.
- **Docs sai.** Cứ nói thẳng là sai.

### 2.3 Quy trình khi muốn lệch

**Với thay đổi nhỏ** (mức GỢI Ý, hoặc MỀM mà không ảnh hưởng file khác):
> Cứ làm. Nói một câu trong phần tóm tắt: *"Docs ghi X, tôi dùng Y vì Z."*

**Với thay đổi có ảnh hưởng** (mức MỀM, chạm nhiều file hoặc đổi contract):
1. Kiểm bằng chứng, nêu gọn *vấn đề → contract hiện tại → cách sửa → đánh đổi* trong tracker.
2. Nếu vẫn đạt mọi DoD và K1–K13, triển khai và kiểm chứng trong phạm vi công việc đã giao.
3. Cập nhật docs cùng thay đổi; ghi ADR nếu quyết định khó đảo ngược hoặc đổi contract.

**Với thay đổi mức CỨNG**: luôn phải hỏi, không có ngoại lệ.

> [!important] Điều tệ nhất không phải làm trái docs
> Điều tệ nhất là **âm thầm** làm trái docs, hoặc **cố ép code cho khớp một plan sai** rồi để lại một chỗ xấu không ai hiểu vì sao. Cả hai đều tệ hơn việc nói ra.

### 2.4 Docs phải được cập nhật, không để trôi

Khi code đã lệch khỏi docs mà lệch đó là đúng → **sửa docs**, đừng để docs thành tài liệu chết. Việc này là một phần của công việc, không phải "để sau".

### 2.5 Docs tinh gọn — bài học **ghi đè** plan cũ, không chồng thêm một tầng

Cập nhật docs nghĩa là **sửa câu sai thành câu đúng**, không phải viết thêm một mục *"bài học rút ra"*
bên dưới câu sai. Hai câu mâu thuẫn cùng nằm trong một file thì người đọc sau phải tự đoán câu nào
còn hiệu lực — và họ sẽ đoán sai.

| Tình huống | Làm | **Không** làm |
|---|---|---|
| DoD được phát biểu lại | Sửa thẳng bảng §1 của plan | Giữ bảng cũ rồi thêm callout *"chỗ nào lệch thì ADR-xxx thắng"* |
| Một phép đo bác bỏ số cũ | Thay số trong plan/ADR; số cũ chỉ sống ở `benchmarks.md` (có ngày, có commit) | Thêm mục *"đo lại lần 2, lần 3"* vào plan |
| Audit bắt được lỗi | Sửa chỗ plan đã mô tả sai, rồi ghi phần **còn nợ** vào đúng bảng nợ | Thêm §*"Audit vòng N"* thuật lại diễn biến |
| Một bản sửa bị bác bỏ | Xoá mô tả bản sửa đó, viết bản đúng | Kể cả hai và để người đọc chọn |

Plan là **mô tả trạng thái cuối**, không phải nhật ký. Diễn biến ai sửa gì lúc nào đã nằm trong
`git log` và `benchmarks.md`; chép nó vào plan là nhân đôi dữ liệu và làm plan dài gấp ba mà không
thêm một mệnh đề kiểm được nào.

Ngoại lệ duy nhất: một quyết định **thay đổi hướng đi** thì viết ADR — ADR có chỗ cho §Context và
được phép kể vì sao. Plan thì không.

---

## 3. Audit sau khi implement

Sau mỗi milestone (và bất cứ khi nào được yêu cầu audit), quy trình là:

### 3.1 Audit kiểm ba thứ, theo thứ tự

1. **Code có đạt DoD không?** Chạy lệnh, đọc số, so ngưỡng. Không đọc code rồi phán đoán.
2. **Code có đúng ràng buộc an toàn (§4) không?**
3. **Plan có còn hợp lý không?** ← phần hay bị bỏ qua nhất.

### 3.2 Nếu audit phát hiện plan không hợp lý

**Phải nêu ra.** Không âm thầm bỏ qua vì "dù sao cũng làm xong rồi".

Báo cáo audit khi đó gồm:

```
PHÁT HIỆN: <plan nói gì, thực tế cho thấy gì>
BẰNG CHỨNG: <số đo, output test, hoặc đoạn code cụ thể>
ĐỀ XUẤT: <sửa plan thế nào>
ẢNH HƯỞNG: <milestone nào sau đó bị đổi theo>
```

Ví dụ đúng:
> **PHÁT HIỆN**: Plan M6 giả định closure table cần thiết để đạt p95 < 200 ms. Đo thực tế: recursive CTE với index `(parent_id) INCLUDE (child_id)` đạt p95 = 140 ms trên 100k cell.
> **BẰNG CHỨNG**: `docs/benchmarks.md` dòng 2026-09-14, k6 output đính kèm.
> **ĐỀ XUẤT**: Vẫn làm closure table **nhưng** đổi mục đích — không phải để đạt SLO mà để học đánh đổi ghi/đọc. Ghi rõ trong ADR-006 rằng ở quy mô này CTE là đủ, closure table chỉ thắng từ ~1 triệu cell.
> **ẢNH HƯỞNG**: M6 giữ nguyên thời lượng. Cập nhật §6.4 scope.md.

### 3.3 Audit không được tự sửa code

Agent audit độc lập giữ **read-only** và báo cáo findings để bảo toàn tính khách quan. Agent triển khai được quyền sửa các findings thuộc phạm vi công việc hiện tại, rồi yêu cầu review độc lập và kiểm lại. Chỉ quyết định đổi contract cứng mới cần chủ repo phê duyệt riêng.

### 3.4 Audit theo phễu — full CI là gate cuối, không phải công cụ dò lỗi đầu tiên

Thứ tự bắt buộc trong một lượt re-audit:

1. Đọc diff, kiểm DoD/ràng buộc/docs và chạy các **targeted test** rẻ nhất có thể bác bỏ thay đổi.
2. Nếu còn bất kỳ finding actionable nào: **dừng, báo cáo, không chạy full CI**.
3. Chỉ khi hai bước trên sạch mới chạy `make ci` làm gate cuối.

Đặc biệt, **không chạy `buffer-crash` 200 vòng trong lúc audit vẫn còn lỗi đã biết**. Phép kiểm đó tốn
thời gian và chỉ trả lời contract crash-recovery; nó không phát hiện mâu thuẫn nghiệp vụ, ADR sai,
test false-positive hay bằng chứng vận hành thiếu. Không dùng một CI xanh để thay cho việc đọc và
đánh giá thay đổi.

Nếu người dùng yêu cầu rõ không chạy full CI trong lượt hiện tại thì dừng sau targeted checks, kể cả
khi audit đã sạch, và ghi rõ `full CI chưa chạy theo yêu cầu`.

### 3.5 File audit là file **tạm**, và có vòng đời bắt buộc

Audit được phép **xin một file tạm** để giữ danh sách lỗi bắt được — `docs/audit-<milestone>-<ngày>.md`
— vì một lượt sửa dài sẽ làm mất findings nếu chúng chỉ nằm trong hội thoại. File đó có ba luật:

1. **Xin trước, không tự tạo.** Nói rõ nó là file tạm và điều kiện xoá là gì.
2. **Chỉ chứa findings và trạng thái sửa.** Kết luận nào cần sống lâu hơn — quyết định, số đo, ràng
   buộc mới — phải được chuyển vào ADR, `benchmarks.md` hoặc plan **ngay lúc đóng finding đó**, không
   để dồn tới lúc xoá.
3. **Sửa xong hết thì xoá file, cùng mọi dòng trỏ tới nó.** Không có file audit nào được sống qua
   milestone của nó. Còn nợ mở thì nợ đó đi vào bảng nợ của plan (`N-<milestone>-<n>`), không phải
   lý do giữ file lại.

Áp dụng §2.5 khi chuyển: bản sửa **ghi đè** chỗ plan đã mô tả sai. Chuyển đúng cách thì lúc xoá file
không mất gì — nếu thấy tiếc khi xoá, nghĩa là bước 2 chưa làm.

---

### 3.6 Giao audit cho một agent khác — thứ tự nguồn sự thật, rules of engagement, khung phát hiện

Dự án chạy **audit độc lập** cuối mỗi milestone: một agent **không tham gia viết code** đọc repo và
trả lời đúng một câu — *milestone này có đóng được không*. M3 chứng minh nó đáng làm: các vòng audit
ở đó bắt được năm blocker và bốn lỗi thực thi mà chính người viết code đã đọc qua và không thấy.

**Ba lỗi đã lọt qua một vòng audit rồi mới bị bắt ở vòng sau.** Đưa thẳng vào prompt, đừng để auditor
tự tìm ra:

1. **Đọc hệ thống đang chạy, không đọc diff.** Một vòng audit M3 phát hiện container `ingestion` vẫn
   là image từ **hai ngày trước**: mọi thứ vòng trước tuyên bố *"đã wire"* đều đúng trong code và
   **chưa từng chạy**. Diff xanh, runtime cũ. Luôn hỏi *thứ tôi vừa đọc trong code có đang chạy trong
   process nào không* — kiểm bằng `docker inspect ... --format '{{.Created}}'`, bằng biến môi trường
   thật, bằng một phép thử end-to-end; **không** bằng việc file `.cs` trông đúng.
2. **So lời hứa trong comment với code ngay bên dưới nó.** M3 có một comment viết *"không file nào vào
   `processed` mà chưa giữ bản gốc"* và **ba dòng dưới** là đúng cái nhánh đó. Mỗi mệnh đề khẳng định
   trong comment/XML doc là một **test case**; không tìm được test cho nó thì đó là một phát hiện.
   Comment càng tự tin thì càng đáng kiểm.
3. **Đọc nguyên văn contract, không đọc bản diễn giải của người sửa.** `scope.md` nói legal hold chặn
   *"mọi retention policy"*; một bản sửa chỉ tắt retention của raw và **tự miễn trừ** cho rollup bằng
   một lý do nghe hợp lý. Một luật có ngoại lệ do chính người thực thi tự khoét thì không còn là luật.
   Khi bản sửa nói *"X không áp dụng ở đây vì Y"* — kiểm **Y**, đừng kiểm X.

**Thứ tự nguồn sự thật.** Hai tài liệu mâu thuẫn thì **cái trên thắng**, và mâu thuẫn đó **tự nó là
một phát hiện**:

1. `AGENTS.md` §4 — ràng buộc **K1–K13**. Không milestone nào được nới.
2. `docs/adr/ADR-*.md` — quyết định đã `Accepted`. ADR **không sửa**; đổi ý thì viết ADR mới.
3. `docs/scope.md` — hợp đồng gốc.
4. `docs/plans/M*.md` — DoD và checklist của milestone.
5. Code, migration, `docker-compose.yml`.
6. Hệ thống đang chạy.

**Rules of engagement — mặc định cho mọi audit:**

- **Read-only.** Không sửa file, không commit, không recreate container, không chạy script làm đổi dữ
  liệu persistent (`make telemetry-backfill`, các lab `*-lab`, `down -v`). Working tree phải sạch khi
  xong. Query read-only lên DB đang chạy thì được — nhưng `EXPLAIN ANALYZE` **chạy thật** câu lệnh,
  nên chỉ dùng với `SELECT`.
- **Không in giá trị credential**, kể cả credential dev-only. Nêu **tên khoá**, không nêu giá trị.
  Lỗi này đã xảy ra hai lần trong M3 qua thông báo lỗi của `wget` và `docker inspect` — redact
  **trước**, không redact sau.
- **Không tin số đã ghi.** Số trong `benchmarks.md` là bằng chứng *đã tuyên bố*. Chạy lại được mà rẻ
  thì chạy lại; không thì nói rõ là chưa kiểm.
- Phân biệt rạch ròi **đã đo** / **đã đọc trong code** / **suy luận**. Mỗi phát hiện phải nói nó
  thuộc loại nào.
- Kết thúc bằng **những gì chưa kiểm được và vì sao**. Bắt buộc — một audit không nói mình chưa kiểm
  gì là một audit không dùng được.

**Khung phát hiện** — mỗi phát hiện đúng năm phần:

```
[P1|P2|P3] <một câu, nói cái SAI chứ không nói cái THIẾU>
PHÁT HIỆN : chuyện gì, ở đâu (đường dẫn:dòng)
BẰNG CHỨNG: output lệnh / trích code / query — tái lập được
CONTRACT  : điều khoản nào bị vi phạm, trích nguyên văn
ẢNH HƯỞNG : hỏng gì trên dây chuyền, hoặc auditor sẽ không trả lời được câu nào
ĐỀ XUẤT   : ≥ 2 đường, nói rõ đường nào cần chủ repo duyệt
```

**P1** — vi phạm K1–K13, mất dữ liệu, hoặc một DoD được tuyên bố đạt mà không đạt. **P2** — contract
và thực tế lệch nhau; đúng nhưng không kiểm được. **P3** — nợ kỹ thuật đã biết, ghi để không quên.
**Không đề xuất giải pháp cho P3 nếu chưa được hỏi**: audit nói *cái gì sai*, sửa là lượt khác (§3.3).

**Sau khi nhận báo cáo:** findings vào file tạm theo §3.5. Mỗi quyết định **đổi contract** đi kèm một
ADR — sửa `scope.md` mà không có ADR là sửa DoD cho khớp kết quả, kể cả khi lý do đúng. Việc cần quyền
truy cập hoặc thao tác ngoài công cụ của agent được ghi rõ để chủ repo hỗ trợ; teach-back không là gate.

---

## 4. Ràng buộc kỹ thuật cứng của dự án

Vi phạm những điều dưới đây là bug, không phải lựa chọn phong cách. Chúng được ép bằng analyzer và architecture test (`tests/Architecture`).

| # | Ràng buộc | Vì sao |
|---|---|---|
| K1 | Cấm `DateTime.Now` / `DateTime.UtcNow`. Dùng `TimeProvider` | Saga nhiều ngày phải test được bằng `FakeTimeProvider` |
| K2 | Cấm `DateTime` trong entity và event. Dùng `DateTimeOffset` | Hai site khác timezone, một site có DST |
| K3 | Mọi bảng, event, query có `SiteId`. Filter ép ở server, không tin client | Rò rỉ cross-site là lỗi bảo mật |
| K4 | Event store và genealogy là **append-only**. Không `UPDATE`, không `DELETE` | Traceability là hồ sơ pháp lý |
| K5 | Sửa sai bằng bút toán bù trừ có lý do + người thực hiện | Cùng lý do K4 |
| K6 | Mọi event có `[EventVersion(n)]` ngay từ v1. Không sửa golden file cũ | Code năm 2036 phải đọc được event năm 2026 |
| K7 | Mọi command handler idempotent | Bus là at-least-once |
| K8 | Functional Block không reference FB khác trực tiếp — chỉ qua Contracts + bus | Ranh giới bounded context |
| K9 | Domain layer không reference EF Core / Npgsql / MassTransit | Testability |
| K10 | Mendix chỉ đi qua Public Object Model và Command API. **Không** connection string tới DB của .NET | Bounded context |
| K11 | Service ở `it-net` không có route tới `ot-net` | Ranh giới OT/IT là ranh giới an ninh |
| K12 | Ingestion không gọi App bằng HTTP đồng bộ. Chỉ qua bus | MES down không được làm dừng dây chuyền |
| K13 | Secret không nằm trong file commit lên git | Hiển nhiên, nhưng vẫn hay quên |

Nếu một ràng buộc trong số này **cản trở thật sự**, đó là dấu hiệu thiết kế sai ở chỗ khác. Nêu ra, đừng lách.

---

## 5. Cách làm việc mong đợi

### 5.1 Đơn vị công việc có thể kiểm chứng độc lập

Chia việc thành lát cắt có mục tiêu, diff và cách kiểm chứng rõ. Agent triển khai liên tục qua các lát cắt và milestone trong phạm vi dự án, ghi bằng chứng vào [tracker hoàn thành](docs/plans/project-completion.md). Agent tự commit/push phần đã kiểm chứng; không dừng triển khai để chờ review theo từng commit.

### 5.2 Conventional Commits

```
<type>(<scope>): <mô tả ngắn, tiếng Anh, thể mệnh lệnh>
```

`type`: `feat` · `fix` · `docs` · `test` · `refactor` · `perf` · `build` · `ci` · `chore`
`scope`: tên FB hoặc thành phần — `traceability`, `ingestion`, `host`, `infra`, `mendix`, `pom`…

Ví dụ: `feat(traceability): add optimistic concurrency to event store`

### 5.3 Ngôn ngữ

| Nội dung | Ngôn ngữ |
|---|---|
| Code, tên biến, tên hàm, tên bảng | Tiếng Anh |
| Commit message | Tiếng Anh |
| Comment trong code | Tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/nghiệp vụ tiếng Anh), và chỉ khi giải thích **vì sao**, không phải **cái gì** |
| Tài liệu trong `docs/`, ADR | Tiếng Việt (giữ nguyên thuật ngữ tiếng Anh) |
| Giải thích cho người dùng | Tiếng Việt |

### 5.4 Trước khi bắt đầu một milestone

1. Đọc `docs/scope.md` phần milestone đó **và** phần domain/contract liên quan.
2. Đọc plan trong `docs/plans/`.
3. Tự xử lý lựa chọn triển khai thông thường. Chỉ hỏi khi cần quyền truy cập/thao tác chỉ người dùng làm được hoặc khi phải đổi contract cứng; gom câu hỏi theo lần phụ thuộc đầu tiên.

### 5.5 Hỏi ít, hỏi đúng lúc

Chủ repo đã nói rõ: **hỏi dồn nhiều câu thì phiền**. Nguyên tắc:

- Việc gì tự quyết được theo §2.3 thì tự quyết, nói một câu.
- Chỉ hỏi khi câu trả lời **đổi việc phải làm**.
- Gom câu hỏi vào một lần, đầu milestone. Không rải rác.
- Mỗi câu hỏi phải kèm: *nếu chọn A thì sao, chọn B thì sao*.

### 5.5.1 Phân công nhiều agent

Khi công cụ hỗ trợ và công việc có ranh giới độc lập, giao subagent phạm vi file/contract rõ, tránh sửa trùng. Codex chịu trách nhiệm tích hợp, kiểm chứng và kết luận; reviewer độc lập chỉ đọc. Ưu tiên Sol-6 ở mức medium/high cho phần triển khai và Astra cho review khó khi phù hợp. Không còn yêu cầu dùng Claude CLI hay pin model Claude cũ. Agent chính điều phối commit/push để tránh xung đột; phân công không nới DoD.

### 5.6 Mendix — agent hoàn thiện model bằng công cụ được hỗ trợ

Agent chịu trách nhiệm triển khai và kiểm chứng Mendix trong phạm vi dự án bằng Studio Pro MCP,
API, CLI hoặc UI được hỗ trợ. Nếu thao tác chỉ thực hiện được trực tiếp trong Studio Pro mà công cụ
không hỗ trợ, chuẩn bị bước cụ thể và nhờ người dùng đúng phần đó. Không biến bài thực hành thủ công
thành điều kiện đóng milestone.

| Quy tắc | Chi tiết |
|---|---|
| Agent tự thao tác khi công cụ hỗ trợ | Dựng page, domain model, microflow, workflow và chạy kiểm chứng trong phạm vi đã giao |
| MCP tầng đọc | `ped_read_document`, `ped_check_errors`, `list_modules`… Dùng để kiểm chứng thay vì hỏi |
| MCP tầng ghi | Được dùng để hoàn thiện model trong phạm vi dự án; giữ bản sao/khả năng khôi phục và ghi rõ document đã sửa |
| Sau khi ghi bằng MCP | Nói rõ đã tạo/sửa document nào và vì sao |
| **Thao tác và giải thích phải TÁCH RỜI** | Xem §5.6.1 — đây là yêu cầu rõ ràng của người dùng |

Chi tiết đầy đủ ở skill [`mendix-manual`](.claude/skills/mendix-manual/SKILL.md).

**Đăng nhập khi kiểm thử:** Khi kiểm thử đã được giao cho agent,
agent tự đăng nhập bằng tài khoản/mật khẩu người dùng đã cung cấp hoặc nguồn credential
được phép dùng, kể cả đăng nhập lại sau F5. Không yêu cầu người dùng tự nhập chỉ vì trường
đó là mật khẩu. Chỉ nhờ người dùng khi thiếu credential, cần MFA/CAPTCHA hoặc công cụ thật
sự không thao tác được. Không in mật khẩu/token vào output hay ghi vào file được commit;
ẩn output của công cụ có thể echo thao tác nhập mật khẩu trước khi trả kết quả.

#### 5.6.1 Bố cục bắt buộc khi hướng dẫn thao tác Mendix

Người dùng đã nêu rõ: **trộn thao tác với giải thích làm các bước rất khó đọc.** Người đang
ngồi trước Studio Pro cần bấm, không cần đọc lý do song song.

```
### Thao tác

Mở:      <đường dẫn .mpr tuyệt đối>
Branch:  <tên branch>
Mục tiêu: <một câu>

1. <một hành động>
2. <một hành động>

Báo lại: <đúng một thứ>

---

### Giải thích

<viết tự do>
```

Luật cho khối **Thao tác**: mỗi bước một hành động · **không có chữ "vì", "để", "do"** ·
đường dẫn menu đầy đủ · giá trị chính xác trong `code` · không nêu phương án thay thế ·
tối đa 7 bước một khối · kết thúc bằng đúng một thứ cần báo lại.

Khối **Giải thích** đặt sau, ngăn bằng `---`, được dài. Không có gì đáng giải thích thì bỏ hẳn.

#### 5.6.2 Phát hiện skill sai thì sửa skill NGAY trong phiên

Skill `mendix-manual` chỉ có giá trị nếu nó tích luỹ được thứ **đã kiểm chứng**. Mỗi lần thực
tế trong Studio Pro khác với thứ agent nói, đó là một lỗ hổng phải vá ngay — không ghi chú
"để sau", không đợi cuối milestone.

**Bắt buộc sửa `references/studio-pro-traps.md` §0 khi gặp bất kỳ điều nào sau:**

| Dấu hiệu | Ghi lại gì |
|---|---|
| Tên menu, nút, trường agent đưa ra **không tồn tại** | Tên thật, kèm đường dẫn đầy đủ |
| Phiên bản module/Studio Pro khác docs hoặc khác plan | Số version thật và hệ quả |
| Một bước sinh ra lỗi mà agent không lường trước | Mã lỗi, nguyên nhân, cách sửa đã chạy được |
| Danh sách dependency thiếu hoặc thừa | Danh sách đúng, kèm điều kiện version |
| Giả thuyết agent nêu ra được xác nhận hoặc bác bỏ | Kết luận dứt khoát, xoá chữ "giả thuyết" |

**Quy tắc ghi**: chỉ ghi thứ đã **thấy tận mắt** trong phiên — ảnh chụp của người dùng, output
lệnh, hoặc lỗi thật. Không ghi phỏng đoán. Nếu chưa xác nhận thì đánh dấu rõ *đang kiểm* và
quay lại chốt khi có kết quả.

**Nguồn nào được dùng làm giá trị chính xác** — xem bảng trong `SKILL.md` §1. Tóm tắt: ảnh chụp
và §0 thì tin được; blog, Medium, forum, trí nhớ về bản cũ thì không.

Sai một tên menu làm người dùng đi tìm thứ không tồn tại. Sai hai lần thì họ mất tin vào cả
khối thao tác — và lúc đó skill trở thành gánh nặng thay vì công cụ.

### 5.7 Lab phá hoại và phép đo

Lab trong `scope.md` là phép kiểm kỹ thuật khi có rủi ro cụ thể; agent tự chạy trong môi trường thử nghiệm phù hợp và ghi số thật vào ADR hoặc `docs/benchmarks.md`. Không yêu cầu chủ repo dự đoán kết quả trước khi đo. Không lấy một dự đoán hay một lời giải thích làm bằng chứng thay test.

---

### 5.8 Báo cáo nghiệp vụ và tri thức

Khi báo cáo một thay đổi nghiệp vụ, giải thích ngắn thuật ngữ mới, lý do thiết kế và hậu quả nếu làm sai. Giữ [`docs/glossary.md`](docs/glossary.md) nhất quán với thuật ngữ trong model; cập nhật khi thêm khái niệm có ý nghĩa nghiệp vụ. Báo cáo việc agent đã làm và mức kiểm chứng, không suy ra chủ repo đã hiểu. Bài giảng, dự đoán trước phép đo và teach-back chỉ diễn ra khi chủ repo yêu cầu; chúng không chặn triển khai, nghiệm thu hoặc trạng thái milestone.

## 6. Bản đồ tài liệu

| File | Nội dung |
|---|---|
| `AGENTS.md` | Tài liệu này — nguyên tắc làm việc |
| `docs/scope.md` | Scope & design đầy đủ: nghiệp vụ, kiến trúc, domain, contract, 14 milestone |
| **`docs/glossary.md`** | Từ điển nghiệp vụ, cập nhật khi model thêm khái niệm mới |
| `.claude/skills/mendix-manual/` | Skill Mendix: bố cục hướng dẫn, bẫy Studio Pro, tích hợp backend |
| `docs/plans/M*.md` | Plan chi tiết từng milestone và cách kiểm chứng |
| `docs/plans/project-completion.md` | Tracker hoàn thiện M4–M13, gate kỹ thuật và blocker |
| `docs/adr/` | Architecture Decision Records — tối thiểu 18 bản |
| `docs/oef-mapping.md` | Ánh xạ khái niệm Opcenter Execution Foundation → thành phần trong repo |
| `docs/benchmarks.md` | Số đo hiệu năng theo thời gian, kèm ngày và commit hash |
| `docs/runbook.md` | 10 sự cố thường gặp và cách xử lý |
| `docs/event-catalog.md` | Danh mục domain event: version, đã cài đặt chưa, có golden file chưa |

---

## 7. Tóm tắt cho agent đang vội

0. Agent hoàn thiện toàn bộ scope và thu thập bằng chứng kỹ thuật; teach-back/dự đoán không là gate (§0.1, §5.8).
1. **Được tự commit/push phần đã kiểm chứng.** Tiếp tục các đơn vị việc đã được giao, không chờ duyệt theo commit.
2. Docs là bản đồ, không phải đường ray — **được phép làm trái, nhưng phải nói ra và cập nhật docs**.
2b. Cập nhật docs = **sửa câu sai**, không phải viết thêm mục *"bài học"* bên dưới nó (§2.5). Plan mô tả trạng thái cuối; diễn biến ở `git log`.
3. Audit thì kiểm cả plan, không chỉ kiểm code. Plan sai thì đề xuất sửa plan. File audit là file tạm và **phải bị xoá** khi sửa xong (§3.5).
4. §4 là ràng buộc cứng, không lách.
5. Chia việc thành lát cắt có thể review; không dừng ở ranh giới commit.
6. Test đỏ thì nói đỏ.
7. Mendix: agent tự làm qua công cụ được hỗ trợ; chỉ nhờ người dùng phần UI không thể tự thao tác (§5.6).
8. Skill Mendix nói sai thì **sửa skill ngay trong phiên**, không để sau (§5.6.2).
