# AGENTS.md — Nguyên tắc làm việc trong repo này

> Dành cho mọi AI agent (Claude Code, Copilot, Cursor…) và cả người làm việc trong repo `novavolt-mes`.
>
> Tài liệu này **đứng trên** `docs/scope.md` và mọi plan. Khi có mâu thuẫn, theo tài liệu này.

---

## 0. Bối cảnh một dòng

Đây là **NovaVolt v2**: bản mô phỏng tại nhà của một hệ MOM chạy trên nền kiểu Opcenter EX, dựng lại cơ chế nền theo hành vi đã quan sát, nghiệp vụ đồng dạng nhưng đổi tên ([ADR-051](docs/adr/ADR-051-rebuild-theo-mo-hinh-mom-that.md)). v1 nằm ở tag `v1.0`. Repo **public**: không đưa tên, mã, code của dự án thật vào repo (K14, [ADR-052](docs/adr/ADR-052-clean-room.md)). Kiến thức của chủ repo là mục tiêu học tập riêng, **không phải điều kiện nghiệm thu hoặc điều kiện dừng triển khai** ([ADR-039](docs/adr/ADR-039-delivery-without-learning-gates.md)).

### 0.1 Trách nhiệm triển khai của agent

Chủ repo không tự gõ phần lớn code. Agent chịu trách nhiệm triển khai, sửa finding và thu thập bằng chứng cho toàn bộ R0–R13. Tiếp tục qua các đơn vị việc và mốc đã nằm trong scope, không chờ chủ repo commit, trả lời teach-back hoặc dự đoán số đo. Chỉ dừng ở việc thực sự cần quyền truy cập, hoặc quyết định đổi contract cứng.

Kiến thức về hệ thật lấy từ tài liệu tìm hiểu riêng của chủ repo, nằm ngoài repo; bảng đối chiếu tên thật và tên giả ở `docs/private/` (git bỏ qua bằng `.git/info/exclude`). Mỗi hành vi đưa vào code phải dẫn được về một nguồn quan sát trong tài liệu đó; chỗ chưa xác minh thì dựng hai biến thể chọn bằng cấu hình (scope §7), không đoán.

Nền tảng chuyên môn của chủ repo, để agent nhắm đúng chỗ:

| Mảng | Mức | Nghĩa là |
|---|---|---|
| .NET, C#, SQL, system design, distributed systems | **vững** | Không giải thích `record`, DI, index, CQRS là gì. Giải thích **vì sao chọn cách này ở đây** |
| **Nghiệp vụ MOM nhà máy pin** | đã tìm hiểu kỹ ở dự án thật | Giải thích ngắn thuật ngữ khi báo cáo; dùng tên giả trong repo |
| Cơ chế nền Opcenter EX | đã quan sát trên hệ thật | Bám hành vi đã quan sát, không theo phỏng đoán |
| React/Next.js | — | Giao diện v2 (ADR-053) |

Quy tắc báo cáo ở **§5.8**. Nội dung dạy học có thể thực hiện khi được yêu cầu, độc lập với trạng thái chức năng.

---

### 0.2 Cách thực hiện được chủ repo chốt 2026-09-23

Agent nhận toàn bộ phần việc. Không tạo plan riêng cho từng mốc, không chia tiến độ thành lượt chờ theo từng commit. Dùng scope v2 và [tracker](docs/plans/tracker.md); giữ tracker ngắn, triển khai và kiểm chứng liên tục. Chỉ nhờ chủ repo khi thiếu quyền truy cập hoặc phải quyết contract cứng. Commit và push theo §1.1: chủ repo đọc trước.

Làm theo cách của agent, không theo nhịp người học: gom việc thành lô lớn, **không bắt buộc đúng thứ tự mốc** khi phụ thuộc cho phép (scope §5). DoD, K1–K14 và yêu cầu bằng chứng giữ nguyên.

## 1. Quy tắc tuyệt đối — không có ngoại lệ

### 1.1 Chủ repo đọc trước, rồi mới commit

Từ 2026-10-27, agent **không tự commit hay push**. Làm xong một lô thì để thay đổi ở working tree, báo file nào đổi và đã kiểm gì; chủ repo đọc diff. Chỉ khi chủ repo bảo commit thì agent mới gộp thành commit của mốc, gắn tag `rN` (annotated) ở commit cuối của mốc, push branch và tag, rồi ghi tag vào [tracker](docs/plans/tracker.md). So code giữa hai mốc: `git diff r0 r1 --stat` hoặc trang compare trên GitHub.

Trước khi commit, kiểm diff, loại secret/artifact tạm, xác nhận các kiểm tra phù hợp đã đạt; trước khi push, xác nhận đúng repository, branch và remote. Không force-push hoặc bỏ thay đổi của người khác khi chưa có chỉ dẫn riêng.

### 1.2 Không phá working copy của người khác

- Không `git reset --hard`, `git checkout -- .`, `git stash drop`, `git clean -fd`.
- Không đổi branch khi working copy đang dirty.

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

Đặc biệt, **không chạy lab dài (tải, chịu lỗi nhiều vòng) trong lúc audit vẫn còn lỗi đã biết**. Các
phép kiểm đó tốn thời gian và không phát hiện mâu thuẫn nghiệp vụ, ADR sai, test false-positive hay
bằng chứng vận hành thiếu. Không dùng một CI xanh để thay cho việc đọc và đánh giá thay đổi.

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

1. `AGENTS.md` §4 — ràng buộc **K1–K14**. Không mốc nào được nới.
2. `docs/adr/ADR-*.md` — quyết định đã `Accepted`. ADR **không sửa**; đổi ý thì viết ADR mới.
3. `docs/scope.md` — hợp đồng gốc.
4. `docs/plans/tracker.md` — DoD và trạng thái của từng mốc.
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

**P1** — vi phạm K1–K14, mất dữ liệu, hoặc một DoD được tuyên bố đạt mà không đạt. **P2** — contract
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
| K1 | Cấm `DateTime.Now` / `DateTime.UtcNow`. Dùng `TimeProvider` | Lịch ca, timer, hạn đo phải test được bằng `FakeTimeProvider` |
| K2 | Cấm `DateTime` trong entity và event. Dùng `DateTimeOffset` | Giờ ca theo múi giờ plant, có DST |
| K3 | Plant là phạm vi deploy và URL: một plant một DB MOM | Như nền tảng thật; không trộn dữ liệu hai plant trong một DB |
| K4 | Lịch sử MTU và genealogy append-only; xóa là xóa mềm (`IsDeleted`). Chỉ job dọn dữ liệu được xóa thật, và phải ghi log | Truy vết là hồ sơ; xóa thật phải có dấu vết |
| K5 | Sửa sai bằng thao tác bù có lý do + người thực hiện (kết luận NC, hoàn tác tháo rã…) | Cùng lý do K4 |
| K6 | Event lên bus và hợp đồng của gateway có `[EventVersion(n)]`. Không sửa golden file cũ | Bên nhận năm sau phải đọc được tin năm nay |
| K7 | Đường tin máy idempotent theo cách hệ thật: trạng thái dòng tin, khóa tự nhiên, không trừ nguyên liệu hai lần. Command khác theo đúng hành vi của nền, có test khẳng định hành vi khi gửi lại | Tin máy là at-least-once; command thì giữ đúng ngữ nghĩa nền tảng |
| K8 | Phụ thuộc chỉ đi xuống: Project → Product → Foundation; Shopfloor và Integration chỉ thấy Foundation. Cùng domain dùng `Query`, khác domain dùng `ProjectionQuery`; pre-check cùng domain với command; extension app không extend extension app | Ranh giới như nền tảng thật; ép bằng test kiến trúc |
| K9 | Handler chỉ dùng `IPlatform` của Foundation để đọc, ghi model; không reference EF Core, client bus. Ngoại lệ: kết nối ra hệ ngoài hoặc SQL tay mà code của team thật cũng mở (LineDB, stored procedure) được dùng driver (Npgsql, SqlClient), chuỗi kết nối lấy từ endpoint hoặc configuration key, ghi ở ADR (ADR-058, chủ repo chốt 2026-10-29) | Testability; giống handler của nền thật chỉ thấy `platform`, trừ đúng những chỗ code thật tự mở kết nối |
| K10 | Giao diện chỉ gọi POM (OData) và command API; không nối thẳng DB, không giữ logic nghiệp vụ | Ranh giới app |
| K11 | LineDB ở `ot-net`; chỉ relay và lệnh đọc tin có quyền vào schema trung gian | Ranh giới OT/IT |
| K12 | Line không bao giờ chờ MOM: MOM chết thì dòng tin đứng ở trạng thái chờ và được gửi lại | Dây chuyền không dừng vì MOM |
| K13 | Secret không nằm trong file commit lên git | Hiển nhiên, nhưng vẫn hay quên |
| K14 | **Clean-room**: không tên, mã, tiền tố, định dạng, câu lỗi, code của dự án thật trong repo. Giữ cấu trúc, đổi bề mặt. Kiểm bằng `make cleanroom`, hook pre-commit, job CI | Repo public; đã push là không rút lại được (ADR-052) |

Nếu một ràng buộc trong số này **cản trở thật sự**, đó là dấu hiệu thiết kế sai ở chỗ khác. Nêu ra, đừng lách.

---

## 5. Cách làm việc mong đợi

### 5.1 Đơn vị công việc có thể kiểm chứng độc lập

Chia việc thành lát cắt có mục tiêu, diff và cách kiểm chứng rõ. Agent triển khai liên tục qua các lát cắt trong phạm vi dự án, ghi bằng chứng vào [tracker](docs/plans/tracker.md). Thay đổi chờ chủ repo đọc rồi mới commit (§1.1).

### 5.2 Conventional Commits

```
<type>(<scope>): <mô tả ngắn, tiếng Anh, thể mệnh lệnh>
```

`type`: `feat` · `fix` · `docs` · `test` · `refactor` · `perf` · `build` · `ci` · `chore`
`scope`: lớp hoặc thành phần — `foundation`, `product`, `project`, `shopfloor`, `integration`, `web`, `infra`, `cleanroom`…

Ví dụ: `feat(foundation): run post-actions inside the command transaction`

### 5.3 Ngôn ngữ

| Nội dung | Ngôn ngữ |
|---|---|
| Code, tên biến, tên hàm, tên bảng | Tiếng Anh |
| Commit message | Tiếng Anh |
| Comment trong code | Tiếng Việt (giữ nguyên thuật ngữ kỹ thuật/nghiệp vụ tiếng Anh), và chỉ khi giải thích **vì sao**, không phải **cái gì** |
| Tài liệu trong `docs/`, ADR | Tiếng Việt (giữ nguyên thuật ngữ tiếng Anh) |
| Giải thích cho người dùng | Tiếng Việt |

### 5.4 Trước khi bắt đầu một mốc

1. Đọc `docs/scope.md` phần mốc đó **và** phần cơ chế/contract liên quan.
2. Đọc mục của mốc trong [tracker](docs/plans/tracker.md) và nợ mở.
3. Tự xử lý lựa chọn triển khai thông thường. Chỉ hỏi khi cần quyền truy cập/thao tác chỉ người dùng làm được hoặc khi phải đổi contract cứng; gom câu hỏi theo lần phụ thuộc đầu tiên.

### 5.5 Hỏi ít, hỏi đúng lúc

Chủ repo đã nói rõ: **hỏi dồn nhiều câu thì phiền**. Nguyên tắc:

- Việc gì tự quyết được theo §2.3 thì tự quyết, nói một câu.
- Chỉ hỏi khi câu trả lời **đổi việc phải làm**.
- Gom câu hỏi vào một lần, đầu mốc. Không rải rác.
- Mỗi câu hỏi phải kèm: *nếu chọn A thì sao, chọn B thì sao*.

### 5.5.1 Phân công nhiều agent

Khi công cụ hỗ trợ và công việc có ranh giới độc lập, giao subagent phạm vi file/contract rõ, tránh sửa trùng, và theo giới hạn phân công người dùng đang áp dụng trong phiên. Agent chính tích hợp, kiểm chứng, kết luận và điều phối commit/push; reviewer độc lập chỉ đọc. Phân công không nới DoD.

### 5.6 Giao diện web (Next.js)

- Web nằm ở `web/`, chia khu theo cách hệ thật chia app; menu và quyền theo role trong token.
- Giao diện theo AWS Console dark theme (`web/DESIGN.md`), màu lấy nguyên từ Cloudscape, không dùng skill thiết kế.
- Chỉ gọi OData và command API (K10); hiện nguyên mã lỗi và câu lỗi trong thân response.
- Mỗi mốc từ R4 làm màn của luồng mình; DoD của mốc gồm chạy luồng chính trên màn (lab Playwright).
- Khi kiểm thử đã được giao, agent tự đăng nhập bằng tài khoản thử trong realm; không in mật khẩu hay token.

### 5.7 Lab phá hoại và phép đo

Lab trong `scope.md` là phép kiểm kỹ thuật khi có rủi ro cụ thể; agent tự chạy trong môi trường thử nghiệm phù hợp và ghi số thật vào ADR hoặc `docs/benchmarks.md` (số v1 nằm ở tag `v1.0`). Không yêu cầu chủ repo dự đoán kết quả trước khi đo. Không lấy một dự đoán hay một lời giải thích làm bằng chứng thay test.

---

### 5.8 Báo cáo nghiệp vụ và tri thức

Khi báo cáo một thay đổi nghiệp vụ, giải thích ngắn thuật ngữ mới, lý do thiết kế và hậu quả nếu làm sai. Giữ [`docs/glossary.md`](docs/glossary.md) nhất quán với thuật ngữ trong model; cập nhật khi thêm khái niệm có ý nghĩa nghiệp vụ. Báo cáo việc agent đã làm và mức kiểm chứng, không suy ra chủ repo đã hiểu. Bài giảng, dự đoán trước phép đo và teach-back chỉ diễn ra khi chủ repo yêu cầu; chúng không chặn triển khai, nghiệm thu hoặc trạng thái milestone.

## 6. Bản đồ tài liệu

| File | Nội dung |
|---|---|
| `AGENTS.md` | Tài liệu này — nguyên tắc làm việc |
| `docs/scope.md` | Scope v2: bốn lớp, cơ chế nền, lộ trình R0–R13, lab sự cố |
| `docs/plans/tracker.md` | Trạng thái từng mốc, bằng chứng, nợ mở |
| `docs/glossary.md` | Thuật ngữ, cập nhật khi model thêm khái niệm mới |
| `docs/oef-mapping.md` | Bảng cơ chế nền và test chứng minh |
| `docs/adr/` | Architecture Decision Records; 001–050 là của v1 |
| `docs/private/` | Riêng tư, không commit: plan đầy đủ, bảng đối chiếu tên thật, danh sách từ cấm, khóa clean-room |
| `tools/cleanroom/` | Kiểm clean-room (K14) |
| `docs/benchmarks.md` | Số đo có ngày, commit và điều kiện đo |
| `web/DESIGN.md` | Thiết kế giao diện: token màu, chữ, bố cục, luật tương tác |

---

## 7. Tóm tắt cho agent đang vội

0. Agent hoàn thiện toàn bộ scope và thu thập bằng chứng kỹ thuật; teach-back/dự đoán không là gate (§0.1, §5.8).
1. **Không tự commit/push.** Để thay đổi ở working tree cho chủ repo đọc; được bảo mới commit, gắn tag `rN`, push (§1.1).
2. Docs là bản đồ, không phải đường ray — **được phép làm trái, nhưng phải nói ra và cập nhật docs**.
2b. Cập nhật docs = **sửa câu sai**, không phải viết thêm mục *"bài học"* bên dưới nó (§2.5). Plan mô tả trạng thái cuối; diễn biến ở `git log`.
3. Audit thì kiểm cả plan, không chỉ kiểm code. Plan sai thì đề xuất sửa plan. File audit là file tạm và **phải bị xoá** khi sửa xong (§3.5).
4. §4 là ràng buộc cứng, không lách.
5. Chia việc thành lát cắt có thể review; không dừng ở ranh giới commit.
6. Test đỏ thì nói đỏ.
7. Giao diện là Next.js, chỉ gọi POM và command API (§5.6, K10).
8. **Clean-room (K14)**: không đưa tên, mã, code của dự án thật vào repo; dùng bảng tên giả trong `docs/private/`.
