# ADR-029 — Rate limit khi flush, và vì sao gateway phải chậm lại thay vì thử lại nhanh hơn

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-28 |
| **Liên quan** | `ADR-027` (HTTP protobuf gateway → ingestion), `ADR-028` (buffer append-only), `ADR-013` (retry + jitter của bus), [`scope.md`](../scope.md) §9/M2 lab #3, [`plans/M2-simulator-ingestion-idempotency.md`](../plans/M2-simulator-ingestion-idempotency.md) §C10 |

---

## Context

`ADR-028` cho gateway giữ dữ liệu khi ingestion chết. Nó **không** nói gì về lúc ingestion sống lại,
và đó mới là lúc nguy hiểm.

Ba sự thật cùng lúc:

1. Mạng OT không chớp cho **một** gateway. Cả khu `FORMATION` mất mạng cùng lúc và nối lại cùng lúc.
2. Buffer 30 phút ở 5.000 msg/s là ~9 triệu message trên đĩa của **mỗi** gateway.
3. Thời điểm mọi gateway nối lại là thời điểm ingestion vừa khởi động — pool connection lạnh, cache
   trống, và bảng dedup của `ADR-030` là một global index đang phải nhận write.

Phản xạ "xả hết thật nhanh cho an toàn" biến ba điều đó thành một cuộc tấn công tự gây ra. Ingestion
sập lần hai, gateway thấy lỗi nên retry dày hơn, và vòng lặp tự siết — **retry storm**. Sự cố mạng 30
giây thành sự cố hệ thống nửa tiếng, và nó xảy ra **sau** khi phần khó (không mất dữ liệu) đã xong.

Ingestion cũng không có cách nào nói "chậm lại" nếu nó chỉ biết nhận hoặc chết. Không có tín hiệu thì
gateway không có gì để nghe, và mọi thứ ở phía gateway chỉ là đoán.

## Decision

Hai cái phanh độc lập, ở hai đầu, vì hai lý do khác nhau.

**Phía ingestion — nói ra rằng mình quá tải.**
Concurrency limiter trên đúng endpoint batch: `MaxConcurrentBatches` transaction đồng thời,
`MaxQueuedBatches` chờ, quá thì trả **`429`** kèm **`Retry-After`**. `429` chứ không phải `503`: dịch
vụ vẫn khoẻ và request vẫn hợp lệ — chỉ là quá nhiều cùng lúc. Health check nằm **ngoài** limiter,
nếu không Docker sẽ restart một container chỉ vì nó bận, và backpressure thành outage.

**Phía gateway — tự giới hạn kể cả khi không ai kêu.**
Token bucket trên số message mỗi giây (`FlushMessagesPerSecond`, burst một giây).

> [!important] Mặc định đã đổi thành **0 (tắt)** — chốt G3, 2026-08-30
> Bản đầu đặt **12.000**, chọn để *"cao hơn N1 đủ để tiêu backlog trong ngân sách D3"*. Lab #3 sau
> đó đo đường ống xả được **13.224 msg/s**, nên 12.000 nằm **dưới** khả năng thật: nó không còn là
> van an toàn mà là thứ duy nhất quyết định tốc độ hồi phục. Cơ chế **ở lại nguyên vẹn** và vẫn là
> công tắc A/B của lab; chỉ có mặc định đổi. Lý do đầy đủ ở §Evidence.

Khi POST hỏng: exponential backoff `FlushRetryDelay` → `FlushRetryMaxDelay`, **cộng jitter**.
`Retry-After` của ingestion là **sàn**, không phải gợi ý — nếu nó lớn hơn backoff của ta thì ta chờ
theo nó.

**Một điểm khác `NvmRetryPolicy` của M1**: jitter ở đây **chỉ nới dài, không rút ngắn**. `NvmRetryPolicy`
dùng jitter đối xứng vì ở đó không có ai đưa ra sàn. Ở đây có, và jitter đối xứng sẽ cho gateway quay
lại **sớm hơn** đúng cái mốc server vừa nói là nó chịu được. Nới một phía vẫn phá được sự đồng pha —
thứ duy nhất jitter cần làm.

Metric: `gateway.flush.rate`, `gateway.flush.throttled` (server bảo chậm), `gateway.flush.rate_limited`
(ta tự chậm). Ba cái, không phải hai: một đường depth đi xuống chậm có hai nguyên nhân hoàn toàn khác
nhau, và lab #3 tồn tại để phân biệt đúng hai nguyên nhân đó.

## Consequences

**Được**

- Backlog tiêu **đều** thay vì tiêu theo xung. Ingestion thấy tải phẳng, không thấy tường.
- Gateway có một hành vi xác định khi ingestion kêu, thay vì phụ thuộc vào timeout của `HttpClient`.
- Số throttle đọc được, nên "vì sao chậm" trả lời được bằng số chứ không bằng phỏng đoán.

**Mất / phải chịu**

- Rate limit là một **con số đoán trước**. 12.000 msg/s đúng cho một gateway ở N1; mười gateway cùng
  xả vẫn có thể vượt sức ingestion, và tầng bảo vệ còn lại lúc đó chỉ là `429`. M13 mới có số liệu
  nhiều site để chỉnh.
- Backlog lớn tiêu **lâu hơn** so với xả không giới hạn. Đây là đánh đổi có chủ ý, và lab #3 phải đo
  xem cái giá đó mua được gì.
- Thêm một tham số vận hành phải hiểu mới chỉnh đúng. Đặt bằng 0 là tắt hẳn — có ích cho lab, nguy
  hiểm cho production.

**Giới hạn còn để ngỏ**

- Jitter tính một lần cho mỗi lần retry của **một** process. Nó tách được n gateway khỏi nhau, không
  tách được n batch trong cùng một gateway — nhưng gateway chỉ có một batch outstanding một lúc, nên
  giới hạn này không chạm tới đường đi thật.
- Không có adaptive rate: gateway không tự nâng tốc khi thấy ingestion rảnh. Cần số liệu của C16
  trước khi thêm một vòng điều khiển nữa.
- **Trần backoff `FlushRetryMaxDelay = 30 s` chi phối thời gian phục hồi, và giờ đã có số** — ghi thêm
  2026-08-30 sau khi lab D3 bấm giờ từ đúng chỗ DoD nói (`benchmarks.md`, các dòng `working tree J7`). Đo được:

  ```
  22:41:19  flush failed 1 times; retrying after 00:00:02   (Connection refused)
  22:44:05  accepted a flush again after 7 failures; buffer depth=7331
  22:44:05.120 -> .922   xa 7.331 record, depth ve 0
  ```

  **166 giây ngủ, 0,8 giây truyền** — tức ≈ **9.100 record/s** khi nó thật sự làm việc. Bảy lần hỏng liên
  tiếp: 2+4+8+16+30+30+30 giây, cộng jitter 25 %. Chạy lại lần nữa: **cũng 7 lần hỏng**, drain **180 giây**.
  Với outage 120 giây thì lịch backoff **luôn** đưa gateway vào vùng chờ 30 giây trước khi backend quay lại,
  nên ~180 giây là giá trị thường trực; lần **46 giây** đo trước đó là ngoại lệ. Ngân sách của D3 là **180
  giây**, nên mệnh đề *"backlog tiêu hết dưới 3 phút"* hiện do lịch retry quyết định chứ không do đường ống.

  Câu hỏi thiết kế bị lộ ra, chưa quyết: `429`/`503` + `Retry-After` là receiver nói *"tôi bận, lùi ra"*,
  còn `Connection refused` là *"không có ai ở nhà"*. ADR này cho cả hai **cùng một** đường backoff, nhưng
  chỉ trường hợp đầu có thứ để bảo vệ khỏi quá tải. Nhánh thứ hai không đáng nhận trần 30 giây. Quyết ở
  chỗ nào thì gắn với **N15**, không phải với rate limit.

- **ĐÍNH CHÍNH 2026-08-30, cùng ngày: kết luận nhân quả ngay trên đây là SAI, các con số thì không.**
  Log 166 giây là thật, nhưng nó đo từ lần **fail đầu tiên** — tức từ giữa một outage 120 giây có chủ ý —
  chứ không phải từ lúc backend sẵn sàng. Thứ giữ D3 ở 181 rồi 180 giây là **vòng đợi của lab**: nó đòi
  `buffered == forwarded`, hai counter đếm theo đời tiến trình, trong khi buffer và cursor sống qua
  restart, nên chúng lệch nhau một khoản hoàn toàn hợp lệ (đo được: `depth=0` mà `33616 != 33673`) và
  đẳng thức đó không bao giờ đúng. Sau khi lab chốt đích bằng `forwarded` tại snapshot hữu hạn cộng
  backlog đo ở chính snapshot ấy, cùng lab và cùng tham số cho **drain 37 giây** với `throttled = 0` và
  `rate_limited = 0`.

  Vậy giá thật của trần 30 giây là **một nhịp chờ ~30 giây sau khi backend sẵn sàng**, nằm gọn trong ngân
  sách 180 giây — không phải 166 giây và **không** chặn D3. Câu hỏi *Connection refused* vs *503* ở đoạn
  trên **vẫn mở và vẫn đáng hỏi**, nhưng nó không còn được biện minh bằng *"D3 không có đường đạt ổn
  định"*; muốn quyết thì phải có một phép đo nói trần đó tốn gì trên dây chuyền thật. Bằng chứng:
  `benchmarks.md` các dòng `working tree J8`.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Không rate limit, chỉ dựa vào `429` | Ingestion phải **bị đau trước** thì mới có ai chậm lại. Với dedup key là global index, "đau" có thể là lock contention chứ không phải 429 kịp thời |
| Chỉ rate limit, không nghe `Retry-After` | Một con số cố định không biết ingestion đang gánh mấy gateway. Server là bên duy nhất biết tổng tải |
| Sleep cố định giữa các batch | Gateway rảnh cũng bị phạt, và tốc độ thật phụ thuộc kích thước batch — không phát biểu được thành một con số msg/s |
| `System.Threading.RateLimiting` phía gateway | Nó tính theo **request**, còn ngân sách thật đo bằng **message**. Một batch có thể mang 1 hay 10.000 message |
| Circuit breaker thay vì backoff | Breaker mở nghĩa là ngừng thử — đúng cho lỗi vĩnh viễn, sai cho quá tải, vì quá tải tự hết khi ta chậm lại |

## Evidence

Unit test (`FlushPacingTests`, `StoreAndForwardFlusherTests`) khoá phần logic:

- token bucket tiêu burst rồi mới pace; batch lớn hơn burst **vẫn qua** (nếu không, cursor kẹt vĩnh
  viễn và buffer lớn dần tới ngưỡng đĩa);
- backoff nhân đôi tới trần và **không tràn** sau nhiều giờ hỏng;
- 200 lần rút jitter với `Retry-After = 4 s` đều nằm trong `[4 s, 5 s]` — **không lần nào** dưới sàn;
- `Retry-After` nhỏ hơn backoff của ta **không** làm ta nhanh lên.

Integration test (`IngestionAdmissionControlTests`) khoá phần hợp đồng phía server: ingestion bão hoà
trả `429` + `Retry-After: 3`, và `/health/live` **vẫn 200** trong lúc đó.

**Lab §5.C10.2 (D3) — đã chạy 2026-08-29.** Tắt `ingestion` + `timescale` + `rabbitmq` 120 giây
giữa lúc simulator chạy:

| Số | Giá trị |
|---|---|
| Message trên đĩa sau 2 phút | **6.272** (27.091.330 byte) |
| Row lệch sau khi bật lại | **0** (23.070 = 23.070) |
| Simulator / gateway restart | **0 / 0** |
| Thời gian tiêu backlog | **185 s** (ngân sách 180 s — **trượt 5 giây**) |
| `gateway.flush.throttled` | **0** |
| `gateway.flush.rate_limited` | **0** |

**Hai con số cuối là kết luận thật của ADR này, và nó không tâng bốc quyết định.** Rate limit
**chưa từng chạm** trong lab D3, và ingestion **chưa từng trả `429`** kể cả ở D2 với 5.000 msg/s
bắn vào. Ở quy mô một gateway trên máy này, cả hai cái phanh đều là phanh chưa dùng tới.

Lần chạy D3 **đầu tiên** lại có ích hơn: gateway restart **6 lần** trong 2 phút backend chết, và
backlog tiêu 183 giây. Nguyên nhân không phải rate limit mà là một bộ lọc `catch`:
`HttpClient` timeout ném `TaskCanceledException`, kế thừa `OperationCanceledException`, nên
`catch ... when (exception is not OperationCanceledException)` cho nó lọt và host dừng. Sửa xong
thì restart về 0. Bài học thuộc về ADR-028 nhiều hơn ADR này: **thứ giữ được dữ liệu khi backend
chết là buffer bền vững, không phải chính sách xả** — và một chính sách xả tinh vi trên một
process tự chết là chính sách không bao giờ được chạy.

**Lab #3 (`scope.md` §9/M2 — buffer 30 phút, xả A/B) CHƯA CHẠY.** Đây là lab **duy nhất** đo được
ADR này có đáng tồn tại không, và nó vẫn còn nợ. Theo `AGENTS.md` §3.2: nếu lần A (tắt rate limit)
cũng không sao, thì ADR này đang giải một vấn đề chưa tồn tại ở quy mô này, và điều đó phải được
ghi thẳng vào đây thay vì giữ rate limit vì nó là một pattern nổi tiếng. Bằng chứng hiện có
**nghiêng về hướng đó**, và nó chưa đủ để kết luận.

## Evidence — lab #3 đã chạy (2026-08-29, R8)

Cho tới R8, mục Evidence của ADR này vẫn ghi *"lab #3 chưa chạy"*. Nay đã chạy đúng hình
dạng plan §5.C10.3 mô tả: nạp **30 phút ở N1** với ingestion tắt, chụp lại buffer, rồi xả
**cùng một backlog** hai lần.

| # | Số plan đòi | Giá trị |
|---|---|---|
| 1 | Kích thước buffer sau 30 phút | **9.071.178 record · 1,55 GiB** |
| 2 | p95 lag khi xả **không** rate limit | **1.987,256 s** — và **0** lần `429`/`503` |
| 3 | Thời gian tiêu backlog khi **có** rate limit | **851 s** (5.070 lần bị giữ lại) |
| 4 | Số row lệch | **0** ở cả hai lần (9.071.178 = 9.071.178) |

### Điều lab này nói ra

**Rate limit hiện tại chỉ làm chậm việc hồi phục, không ngăn được gì đo được.**

- Lần A xả hết trong **686 s**, tức **13.224 msg/s**. Ingestion **chưa từng** trả `429` hay `503`
  — không hề có tín hiệu quá tải nào để mà phản ứng.
- Lần B xả hết trong **851 s**, chậm hơn **165 giây (+24%)**, và 5.070 lần bị giữ lại bởi chính
  limiter của mình chứ không phải bởi ingestion.
- Cả hai lần **không mất row nào**. Rate limit không làm hỏng tính đúng đắn; nó chỉ không mua
  được gì.

Trần **12.000 msg/s** đã được chọn để *"nằm trên N1 để còn đuổi kịp backlog"*. Thực tế đường
ống xả được **13.224 msg/s**, nên con số đó nằm **dưới** khả năng thật và trở thành ràng buộc
duy nhất đang bóp đầu ra. Đây là một con số được đoán trước khi có dữ liệu, và nay có dữ liệu.

### G3 đã chốt — hướng 1, ngày 2026-08-30

**Bỏ trần tĩnh khỏi mặc định. Giữ nguyên phần phản ứng `429`/`503` + `Retry-After`. Giữ nguyên cơ
chế và cái knob.**

Vì sao hướng 1 chứ không phải 2 hay 3:

- **Closed-loop thắng open-loop.** Phần tôn trọng `Retry-After` trả lời một tín hiệu **có thật, do
  bên nhận phát ra**. Trần cố định trả lời một phỏng đoán, và nó chậm lại kể cả khi bên nhận đang
  rảnh. Hệ production ưu tiên vòng kín; vòng hở chỉ là van chặn cuối cùng.
- **Phỏng đoán này sai theo hướng có hại.** 12.000 < 13.224 đo được, nên trần chưa bao giờ là van
  an toàn — nó là ràng buộc *thường trực* của mọi lần hồi phục. Ba lần đo độc lập nói nó chưa từng
  bảo vệ gì: lab #3 lần A và lần B đều **0 lần** ingestion trả 429/503, và D3 chạy với trần 12.000
  cũng ghi `rate_limited = 0`.
- **Hướng 2 (nâng lên 20.000) chỉ dời phỏng đoán đi chỗ khác.** Một con số không ai đo lại mỗi khi
  đổi phần cứng là một con số sẽ sai im lặng ở lần đổi máy đầu tiên. Van an toàn mà không ai hiệu
  chuẩn thì không phải van an toàn.
- **Hướng 3 (giữ nguyên) trả 165 giây hồi phục để mua một lợi ích chưa đo được.** Trên dây chuyền
  thật, 165 giây chậm hơn là 165 giây dữ liệu formation về muộn sau một sự cố mạng.

**Điều lab này KHÔNG chứng minh, và khoản nợ được ghi rõ:** kịch bản retry storm thật là **nhiều
gateway cùng nối lại** sau sự cố cả một khu — đúng thứ glossary mô tả. Một gateway không tạo ra
được nó. Nếu **M13** dựng nhiều gateway và thấy admission control của ingestion không hấp thụ nổi
tổng tải, trần được bật lại — và con số phải đến từ **capacity tổng đo được**, không phải từ một
phỏng đoán thứ hai.

> Đây đúng là bài học `AGENTS.md` §3.2 nêu ở ví dụ closure table: giữ một pattern vì nó nổi tiếng,
> rồi phát hiện ở quy mô này nó chưa giải quyết vấn đề nào. Khác một chỗ: pattern **ở lại trong
> code** vì nó sẽ đúng ở quy mô khác; thứ bị bỏ là **mặc định bật nó lên**.
