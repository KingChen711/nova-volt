# AGENTS.md — Nguyên tắc làm việc trong repo này

> Dành cho mọi AI agent (Claude Code, Copilot, Cursor…) và cả người làm việc trong repo `novavolt-mes`.
>
> Tài liệu này **đứng trên** `docs/scope.md` và mọi plan. Khi có mâu thuẫn, theo tài liệu này.

---

## 0. Bối cảnh một dòng

Đây là **learning project**. Mục tiêu là *người chủ repo hiểu sâu*, không phải *code chạy sớm*. Một giải pháp thông minh mà chủ repo không giải thích được là một giải pháp **thất bại**.

### 0.1 Chủ repo KHÔNG tự gõ code .NET — điều này đổi định nghĩa "xong"

Xác nhận 2026-08-26. Phần lớn code .NET do agent viết từ plan chi tiết. Hệ quả phải nắm trước khi làm bất cứ việc gì:

> **Sản phẩm của một commit không phải code. Sản phẩm là hiểu biết của chủ repo. Code là sản phẩm phụ.**

Ở một dự án bình thường, người ta hiểu code vì đã vật lộn với nó. Ở đây đường đó không tồn tại — nên hiểu biết phải được **giao một cách có chủ đích**, không phó mặc cho việc chủ repo tự đọc diff rồi tự suy ra.

Nền tảng chuyên môn của chủ repo, để agent nhắm đúng chỗ:

| Mảng | Mức | Nghĩa là |
|---|---|---|
| .NET, C#, SQL, system design, distributed systems | **vững** | Không giải thích `record`, DI, index, CQRS là gì. Giải thích **vì sao chọn cách này ở đây** |
| **Nghiệp vụ sản xuất pin / MES / traceability** | **mới** | Đây là chỗ cần giảng. Không giả định biết `formation`, `OCV`, `lot`, `MRB`, `work order` nghĩa là gì |
| Opcenter EF, Mendix | đang học | Giải thích khái niệm, đối chiếu với thứ đang tự dựng |

Quy tắc thi hành ở **§5.8**. Không có ngoại lệ vì "commit này thuần kỹ thuật" — nếu thật sự không chạm nghiệp vụ nào thì nói đúng một câu như vậy, đừng bịa ra nội dung cho đủ mục.

---

## 1. Quy tắc tuyệt đối — không có ngoại lệ

### 1.1 KHÔNG BAO GIỜ tự commit

Agent **không được** chạy:

```
git commit · git push · git merge · git rebase · git tag
gh pr create · gh pr merge
```

Kể cả khi công việc đã xong, kể cả khi test xanh, kể cả khi người dùng nói "làm tiếp đi".

**Được phép**: `git status`, `git diff`, `git log`, `git add` (để chuẩn bị staging).

**Việc của agent khi xong một đơn vị công việc**:
1. Nói rõ đã sửa file nào.
2. Nêu kết quả kiểm chứng (lệnh gì, output ra sao).
3. **Đề xuất** commit message theo Conventional Commits.
4. Dừng lại. Chờ người dùng tự commit.

**Lý do**: chủ repo cần đọc từng thay đổi trước khi nó vào lịch sử. Đây là project để học — mỗi commit là một lần ôn lại.

> Nếu người dùng nói thẳng *"commit giúp tôi"* trong lượt hiện tại thì được, nhưng **chỉ commit đó**, không suy rộng cho lần sau.

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
1. **Dừng trước khi code.**
2. Nêu gọn: *vấn đề thực tế là gì* → *docs nói gì* → *đề xuất gì* → *đánh đổi là gì*. Tối đa 5–6 dòng, không viết luận văn.
3. Chờ xác nhận.
4. Sau khi làm xong: **cập nhật docs** và ghi ADR trong `docs/adr/`.

**Với thay đổi mức CỨNG**: luôn phải hỏi, không có ngoại lệ.

> [!important] Điều tệ nhất không phải làm trái docs
> Điều tệ nhất là **âm thầm** làm trái docs, hoặc **cố ép code cho khớp một plan sai** rồi để lại một chỗ xấu không ai hiểu vì sao. Cả hai đều tệ hơn việc nói ra.

### 2.4 Docs phải được cập nhật, không để trôi

Khi code đã lệch khỏi docs mà lệch đó là đúng → **sửa docs**, đừng để docs thành tài liệu chết. Việc này là một phần của công việc, không phải "để sau".

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

Audit là **đọc và báo cáo**. Muốn sửa → đề xuất, chờ đồng ý, rồi sửa ở lượt sau. Lý do: audit mà vừa sửa vừa đánh giá thì không còn khách quan, và người dùng mất cơ hội tự nhìn thấy vấn đề.

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

### 5.1 Đơn vị công việc = một commit

Mỗi lần làm việc nhắm tới **một commit hoàn chỉnh**: có mục tiêu rõ, có cách kiểm chứng, tự đứng được. Không gộp 5 việc không liên quan; không để lại code chết chờ commit sau.

Xong một đơn vị → dừng, báo cáo, chờ người dùng commit. Không tự chạy tiếp sang đơn vị kế.

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
| Comment trong code | Tiếng Anh, và chỉ khi giải thích **vì sao**, không phải **cái gì** |
| Tài liệu trong `docs/`, ADR | Tiếng Việt (giữ nguyên thuật ngữ tiếng Anh) |
| Giải thích cho người dùng | Tiếng Việt |

### 5.4 Trước khi bắt đầu một milestone

1. Đọc `docs/scope.md` phần milestone đó **và** phần domain/contract liên quan.
2. Đọc plan trong `docs/plans/`.
3. Nêu ngay những điểm cần người dùng quyết định, **trước khi code**, không hỏi giữa chừng.

### 5.5 Hỏi ít, hỏi đúng lúc

Chủ repo đã nói rõ: **hỏi dồn nhiều câu thì phiền**. Nguyên tắc:

- Việc gì tự quyết được theo §2.3 thì tự quyết, nói một câu.
- Chỉ hỏi khi câu trả lời **đổi việc phải làm**.
- Gom câu hỏi vào một lần, đầu milestone. Không rải rác.
- Mỗi câu hỏi phải kèm: *nếu chọn A thì sao, chọn B thì sao*.

### 5.6 Mendix — người dùng tự thao tác, agent hướng dẫn

Ở dự án CGVibe trước đây, agent làm phần lớn việc Mendix qua Studio Pro MCP. **Dự án này
thì không.** Người dùng muốn tự tay thao tác để học Mendix bằng cơ bắp.

| Quy tắc | Chi tiết |
|---|---|
| Agent **viết ra các bước bấm**, người dùng bấm | Đây là mặc định |
| **MCP tầng đọc: luôn được** | `ped_read_document`, `ped_check_errors`, `list_modules`… Dùng để kiểm chứng thay vì hỏi |
| **MCP tầng ghi: chỉ khi người dùng nói rõ trong lượt đó** | Cùng nguyên tắc với §1.1 về commit — không suy rộng sang lượt sau |
| Sau khi ghi bằng MCP | Nói rõ đã tạo/sửa document nào và vì sao |
| **Thao tác và giải thích phải TÁCH RỜI** | Xem §5.6.1 — đây là yêu cầu rõ ràng của người dùng |

Chi tiết đầy đủ ở skill [`mendix-manual`](.claude/skills/mendix-manual/SKILL.md).

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

### 5.7 Lab phá hoại là bắt buộc

Mỗi milestone trong `scope.md` có mục **Lab phá hoại** — cố tình làm sai để thấy pattern giải quyết vấn đề gì. **Không được bỏ qua để tiết kiệm thời gian.** Nếu người dùng muốn bỏ, nhắc lại một lần rồi tôn trọng quyết định.

Kết quả lab phải được **ghi số** vào ADR hoặc `docs/benchmarks.md`. Lab mà không có số thì chưa xong.

---

### 5.8 Nghiệp vụ phải được giao TRƯỚC code, không phải khi được hỏi

Xuất phát từ §0.1. Ba quy tắc, và cả ba đều kiểm được bằng mắt.

#### 5.8.1 Briefing trước, code sau

Mỗi commit chạm nghiệp vụ **bắt đầu** bằng một khối ngắn, **trước** khi gõ dòng code đầu tiên:

```
### Nghiệp vụ của commit này

Khái niệm mới:  <từ> — <nghĩa trong nhà máy, 1-2 câu>
Vì sao có mặt:  <ràng buộc nghiệp vụ nào đẻ ra thiết kế này>
Nếu làm sai:    <hỏng ra sao trên dây chuyền, không phải hỏng ra sao trong code>

---

### Kỹ thuật
```

Tối đa ~15 dòng. Dài hơn là đang viết luận văn, ngắn hơn là đang cho có.

Vế **"nếu làm sai"** là vế quan trọng nhất và cũng là vế hay bị bỏ nhất. *"Enum gộp ba loại state"* nghe như chuyện gu code. *"Một thao tác chuyển kho sẽ vô tình release hàng lỗi, và auditor sẽ bắt được"* là cùng một chuyện, nói bằng ngôn ngữ hậu quả — và đó mới là thứ ở lại trong đầu.

Trước → sau, không phải ngược lại: đọc giải thích **rồi** đọc diff thì chủ repo đang kiểm chứng một thứ mình đã hiểu. Đọc diff trước rồi mới nghe giải thích thì chỉ còn gật đầu.

#### 5.8.2 Không dùng thuật ngữ chưa có trong `docs/glossary.md`

Mọi từ nghiệp vụ xuất hiện trong báo cáo, ADR, plan hoặc comment code **phải** đã có trong [`docs/glossary.md`](docs/glossary.md). Chưa có → **thêm vào trong chính commit đó**, trước khi dùng.

Glossary là tài liệu sống, cùng hạng với `benchmarks.md` và `oef-mapping.md`. Nó không phải nơi chép lại Wikipedia: mỗi mục nói từ đó nghĩa gì **trong nhà máy NovaVolt**, và nếu có chỗ dễ nhầm thì nói luôn (`EOL` có hai nghĩa; "state" có ba loại).

Lý do có quy tắc này chứ không chỉ "nhớ giải thích": giải thích một từ ba lần ở ba chỗ khác nhau thì ba lần đó sẽ dần lệch nhau, và không lần nào tra lại được.

#### 5.8.3 Tách nghiệp vụ khỏi kỹ thuật trong báo cáo

Cùng nguyên tắc §5.6.1 đã đặt cho Mendix, khác trục. Khối **Nghiệp vụ** và khối **Kỹ thuật** ngăn nhau bằng `---`. Không rải thuật ngữ nhà máy vào giữa đoạn nói về `TryParse`, và không rải tên class vào giữa đoạn nói về formation.

#### 5.8.4 Giới hạn của cách làm này — nói ra thay vì giả vờ không có

Đọc giải thích tạo ra **nhận ra**, không tạo ra **nhớ lại**. Chủ repo sẽ gật đầu khi đọc và vẫn tắc khi phải tự nói lại. Đây là rủi ro thật của mô hình "agent gõ, người đọc", và không có quy tắc viết lách nào xoá được nó.

Ba đối trọng, đều đã có sẵn trong repo — agent phải **dùng**, không được bỏ qua cho nhanh:

| Đối trọng | Ở đâu | Cách dùng |
|---|---|---|
| Đọc diff trước khi commit | §1.1 | Đã có. Briefing §5.8.1 làm cho việc đọc đó có nghĩa |
| **Đoán trước khi đo** | §5.7 | Trước mỗi lab phá hoại, agent hỏi chủ repo **đoán con số**, rồi mới chạy. Đoán sai là lúc học được nhiều nhất |
| **Nói lại bằng lời** | DoD của milestone | Cuối mỗi milestone, agent nêu 2–3 câu hỏi "vì sao", chủ repo tự trả lời **không đọc lại tài liệu**. Tắc câu nào thì phần đó chưa xong — hạ trạng thái, đừng đánh dấu `xong` |

Cả ba đều nhẹ. Bỏ cả ba thì dự án 6 tháng này sản xuất ra một repo đẹp và không ai hiểu nó.

## 6. Bản đồ tài liệu

| File | Nội dung |
|---|---|
| `AGENTS.md` | Tài liệu này — nguyên tắc làm việc |
| `docs/scope.md` | Scope & design đầy đủ: nghiệp vụ, kiến trúc, domain, contract, 14 milestone |
| **`docs/glossary.md`** | **Từ điển nghiệp vụ. Thuật ngữ chưa có ở đây thì không được dùng (§5.8.2)** |
| `docs/cau-hoi-cho-dong-nghiep.md` | Câu hỏi để làm rõ dự án thật ở FPT, và bảng chỉnh trọng số scope |
| `.claude/skills/mendix-manual/` | Skill Mendix: bố cục hướng dẫn, bẫy Studio Pro, tích hợp backend |
| `docs/plans/M*.md` | Plan chi tiết từng milestone, chia theo commit |
| `docs/adr/` | Architecture Decision Records — tối thiểu 18 bản |
| `docs/oef-mapping.md` | Ánh xạ khái niệm Opcenter Execution Foundation → thành phần trong repo |
| `docs/benchmarks.md` | Số đo hiệu năng theo thời gian, kèm ngày và commit hash |
| `docs/runbook.md` | 10 sự cố thường gặp và cách xử lý |
| `docs/event-catalog.md` | Danh mục domain event và schema |
| `docs/package-versioning.md` | SemVer từng Functional Block, compatibility matrix |

---

## 7. Tóm tắt cho agent đang vội

0. **Chủ repo không tự gõ code** (§0.1). Sản phẩm của commit là **hiểu biết**, code là sản phẩm phụ. Nghiệp vụ giao **trước** code, không đợi được hỏi (§5.8). Thuật ngữ chưa có trong `docs/glossary.md` thì thêm vào trước khi dùng.
1. **Không tự commit.** Chuẩn bị xong thì dừng và đề xuất message.
2. Docs là bản đồ, không phải đường ray — **được phép làm trái, nhưng phải nói ra và cập nhật docs**.
3. Audit thì kiểm cả plan, không chỉ kiểm code. Plan sai thì đề xuất sửa plan.
4. §4 là ràng buộc cứng, không lách.
5. Một lần làm việc = một commit.
6. Test đỏ thì nói đỏ.
7. Mendix: **người dùng tự bấm** là mặc định; MCP đọc thì tự do, MCP ghi thì phải được nhờ (§5.6). Luôn **tách thao tác khỏi giải thích** (§5.6.1).
8. Skill Mendix nói sai thì **sửa skill ngay trong phiên**, không để sau (§5.6.2).
