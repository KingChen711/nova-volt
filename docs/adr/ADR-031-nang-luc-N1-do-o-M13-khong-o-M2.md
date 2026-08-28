# ADR-031 — Năng lực N1 nghiệm thu ở M9, đo lại ở M13; M2 chỉ nghiệm thu tính đúng đắn dưới tải

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-30 |
| **Liên quan** | `ADR-027`, `ADR-029`, `docs/scope.md` §4 (N1, N2), §9/M2, §9/M9, §9/M13, `docs/plans/M2-simulator-ingestion-idempotency.md` §1 và §2.3, `docs/benchmarks.md` (các dòng D2 ngày 2026-08-30) |
| **Owner approval** | **2026-08-30** — chủ repo xác nhận rõ bốn điểm: (1) giữ nguyên N1 5.000 msg/s và N2 p95 < 5 s; (2) qualification checkpoint **không muộn hơn M9**; (3) requalification ở **M13**; (4) điều kiện rig đo bằng **QoS1 delivery qua no-op subscriber**, không dùng trần no-subscriber. Đổi một DoD cứng là việc của chủ repo, không phải của agent (`AGENTS.md` §2.1) |

---

## Context

D2 của M2 gộp **hai mệnh đề khác loại** vào một dòng: *"≥ 5.000 msg/s duy trì 10 phút, p95 lag
< 5 s"*. Một mệnh đề nói về **tính đúng đắn dưới tải** — cái vào bằng cái ra, không mất, không
trùng. Mệnh đề kia nói về **năng lực** — đường ống này chạy nhanh tới đâu và trên phần cứng nào.

Ba thứ đo được, ngày 2026-08-29 và 2026-08-30, sau khi roadmap D sửa oracle:

1. **Trần của máy đo nằm sát ngưỡng.** R5 đo ba bước: không subscriber **9.925 msg/s** → một
   `mosquitto_sub` QoS 1 không làm gì **5.951** → gateway thật **5.228**. Trần thực dụng của rig
   là **5.228 msg/s**, cao hơn N1 (5.000) đúng **4,5 %**.
2. **Độ phân tán giữa hai lần chạy là 5 %.** Hai lượt 600 giây cách nhau 15 phút cho **4.710,733**
   và **4.465,391** msg/s — và lượt *offer cao hơn* lại nhanh hơn, nên chênh lệch là nhiễu của
   máy chứ không phải hiệu ứng của cấu hình.
3. **Ingestion không phải nút thắt.** Cùng hai lượt: CPU đỉnh **55,6 %** và **61,3 %**, RAM đỉnh
   **83,4** và **91,7 MiB**; receiver sustained bám source tới từng msg/s (**4.711,5** so với
   **4.710,7**). Lab #3 nhìn từ phía kia xác nhận: khi không tranh CPU với MQTT receive, ingestion
   hấp thụ **13.224 msg/s**.

**Điều bị chặn nếu không quyết:** M2 không đóng được, và không đóng được vì một lý do không nằm
trong code M2. Trong toàn bộ DoD của M2, hai con số N1/N2 là thứ duy nhất còn phụ thuộc vào phần cứng — và đó chính là lý do chúng rời khỏi M2.

**Điều đã biết lúc quyết, kể cả phần có thể sai về sau:** chưa ai chạy đường ống này trên máy nào
khác. Việc nó sẽ đạt 5.000 msg/s trên phần cứng rộng rãi hơn là **suy luận từ ba số của R5**, không
phải phép đo. ADR này không khẳng định điều đó.

## Decision

**N1 giữ nguyên 5.000 msg/s và N2 giữ nguyên p95 < 5 s.** Không hạ, không nới thời lượng.

**Chuyển nơi nghiệm thu**, không đổi ngưỡng:

| Mệnh đề | Nghiệm thu ở | Nội dung |
|---|---|---|
| **Đúng đắn dưới tải** | **M2** | Ở tốc độ mà nguồn thực sự phát ra trong 600 giây: source = gateway decode = fsync = forward = row lưu + row dedup, **exact**; EMQX `send_msg.dropped` = **0**; reject = 0; buffer cuối = 0; corrupt/truncated = 0; và **receiver theo kịp nguồn** (sustained ≥ 99 % tốc độ nguồn — không tích luỹ backlog rồi đuổi sau) |
| **Năng lực N1/N2 — qualification** | **M9, không muộn hơn** | ≥ 5.000 msg/s đầu-cuối trong 600 giây với p95 < 5 s, đo trên rig đạt điều kiện vào bên dưới. Phải ở M9 vì **N9** đòi hold cascade 3.000 pack chạy **đồng thời** với tải N1 và không làm nó giảm quá 10 % — không có N1 đã nghiệm thu thì N9 không có nền để so |
| **Năng lực N1/N2 — requalification** | **M13** | Đo lại đúng phép đo đó sau khi M13 thêm OpenTelemetry đầy đủ, trace context xuyên MQTT và chaos test. Ba thứ đó đều nằm trên đường nóng, nên một con số đạt ở M9 **không** tự động còn đạt ở M13 |

### Điều kiện vào của rig — preflight, sửa 2026-08-30 sau J4

Ghi cứng để không ai đo lại trên một cái laptop khác rồi kết luận nhầm: rig phải **giao được**
**≥ 2 × N1 (10.000 msg/s)** ở **QoS 1, tới một subscriber không làm gì** — đo bằng một
`mosquitto_sub` no-op, không phải bằng trần publish khi không có subscriber nào.

**Bản đầu của ADR này đo sai tầng.** Nó viết *"trần không-subscriber ≥ 10.000"*, mà con số đó chỉ nói
broker **nhận** được bao nhiêu. Chính ba số của R5 ở §Context bác bỏ nó: rig hiện tại đạt **9.925
msg/s** khi không có subscriber — tức **gần đạt** điều kiện cũ — nhưng chỉ còn **5.951** khi thêm đúng
một subscriber QoS 1 không làm gì. Phần hao nằm ở **fan-out và ACK của QoS 1**, và đó chính là tầng
mà gateway sống. Một điều kiện vào đọc ở tầng nguồn sẽ cho qua đúng cái rig mà nó được viết ra để loại.

Với ngưỡng đọc ở đúng tầng, rig hiện tại **trượt preflight**: 5.951 < 10.000. Đó là kết luận mong
muốn — nó nói thẳng vì sao phép đo N1 không thể diễn ra ở đây, thay vì để con số 9.925 tạo ấn tượng
ngược lại. **Preflight là điều kiện vào, không phải một phần của phép đo**: rig trượt preflight thì
`NVM_LOAD_ENFORCE_N1=1 make load` không được chạy để lấy kết luận, dù nó vẫn chạy được.

`scripts/load-gate.sh` giữ **một** bộ mã cho cả hai: mặc định chạy chế độ M2 (in số N1 nhưng không
ép), `NVM_LOAD_ENFORCE_N1=1` bật chế độ nghiệm thu năng lực — M9 rồi M13 — ép N1 và p95 thành gate cứng.

**Ranh giới của quyết định này:** nó **không** nói đường ống đạt N1. Nó nói rig hiện tại không trả
lời được câu đó, và nói ai sẽ trả lời.

## Consequences

**Được**

- M2 nghiệm thu đúng thứ M2 xây: **đường vào**. `AGENTS.md` §0.1 và plan §1 đều nói M2 chịu trách
  nhiệm *"cái vào bằng cái ra, không dư không thiếu"* — và mệnh đề đó **đã đạt tuyệt đối**, hai lần,
  trên 2,8 và 2,7 triệu message.
- Con số N1 vẫn được đo và **in ra mỗi lần chạy**, kèm chữ CHƯA ĐẠT khi dưới ngưỡng. Không có
  đường nào để nó lặng lẽ biến mất.
- M9 nhận một phép đo **có điều kiện vào rõ ràng** và một checkbox để tick, thay vì một dòng "đo lại khi nào rảnh".

**Mất / phải chịu**

- **M2 đóng lại với một DoD chưa được chứng minh trên phần cứng thật.** Đây là cái giá, và nó có
  thật: nếu M9 đo ra 3.000 msg/s trên rig đủ mạnh thì kiến trúc có vấn đề mà M2 đã đi qua — và lúc đó bốn milestone
  đã xây tiếp lên trên nó. Đây là lý do checkpoint đặt ở M9 chứ không để tới M13.
- Rủi ro loại này có tên: **dời một phép đo khó sang tương lai**. Thứ duy nhất chống lại nó ở đây là
  điều kiện vào định lượng và việc `make load` vẫn in N1 mỗi lần chạy.
- Người đọc `scope.md` §4 sẽ thấy N1 gắn với M2 trong bảng NFR nhưng nghiệm thu ở M9/M13. Bảng phải
  nói ra điều đó, nếu không nó là một mâu thuẫn im lặng — đúng thứ K7 đã mắc và `ADR-023` vừa phải
  gỡ.
- p95 dời đi **kéo theo N2**, mà M3 lại dựng continuous aggregate trên chính dòng dữ liệu này. M3 phải
  biết p95 chưa được nghiệm thu — và với checkpoint ở M9, M3 tới M8 đều chạy trên một con số chưa ai chấm.

**Việc phát sinh**

- `scope.md` §4: N1 và N2 ghi *"đo ở M2 · qualification ≤ M9 · requalification M13"*.
- `scope.md` §9/M2: ô N1/N2 **rời khỏi** danh sách DoD của M2. Nó không còn là điều kiện đóng M2, nên
  để nó nằm lại dưới dạng checkbox mở là vừa chặn M2 vừa không ai chịu trách nhiệm — thay bằng một
  dòng trỏ tới M9/M13.
- `scope.md` §9/M9 và §9/M13: mỗi nơi nhận **một lệnh chạy được và một DoD checkbox**, không phải một
  câu văn. Một nghĩa vụ không có ô để tick là một nghĩa vụ sẽ trôi — đó chính là J4.
- M9 và M13 chạy `NVM_LOAD_ENFORCE_N1=1 make load` (600 giây, ngưỡng nằm trong script) **sau khi rig
  qua preflight**, và ghi số vào `benchmarks.md` như mọi phép đo khác.
- Nếu M9 đạt N1: tick ô N1 **của M9**, và **không** mở lại ô D2 của M2. D2 nghiệm thu *tính đúng đắn dưới tải* và đã đóng ở M2 bằng phép đo riêng của nó; N1/N2 là mệnh đề khác loại, đã rời sang M9/M13.
- Nếu M9 **không** đạt: đây là chỗ mở lại kiến trúc chặng MQTT → gateway, không phải chỗ hạ N1 — và
  phải giải quyết **trước** N9, vì N9 đo mức suy giảm so với chính N1.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Hạ N1 xuống 4.500 msg/s cho vừa số đo** | Đổi câu hỏi cho vừa câu trả lời: N1 đến từ tải nhà máy, không đến từ cái laptop |
| **Giữ D2 nguyên trạng, để M2 mở vô thời hạn** | Chặn M3–M13 bằng một điều kiện mà rig hiện tại **không thể** quyết. Biên 4,5 % nhỏ hơn nhiễu 5 %: chạy lại bao nhiêu lần cũng chỉ đổi phía may rủi |
| **Chạy lại tới khi có một lượt ≥ 5.000** | Chính là thứ §F5 đã cấm — *"không chạy lại tới khi may mắn xanh"*. Với nhiễu 5 %, đủ số lần thì sẽ có một lượt xanh, và nó không chứng minh gì |
| **Tối ưu tiếp cho tới khi đạt** | R5 đã đo và **bác bỏ** bốn giả thuyết (`FsyncBatchSize`, cửa sổ in-flight, Server GC, cache parse topic), và đo được 40 % phần hao thuộc fan-out của EMQX chứ không thuộc code. Tối ưu tiếp là tối ưu một thứ không đo được lợi ích |
| **Đo D2 với nhiều edge node cho đủ 5.000 tổng** | Đúng hình dạng nhà máy thật và **sẽ** là cách M9 đo. Nhưng làm ở M2 là đổi topology giữa chừng để một phép đo xanh lên, và M2 chưa có gateway thứ hai |

## Evidence

Toàn bộ ở `docs/benchmarks.md`, mục M2, các dòng ghi `working tree D`:

```
offer 5.100 -> 4.710,733 msg/s   p95 dinh 9,144 s   2.826.722 exact   EMQX dropped 0
offer 5.000 -> 4.465,391 msg/s   p95 dinh 9,567 s   2.679.501 exact   EMQX dropped 0
receiver sustained 4.711,522 msg/s / 571 s   CPU 55,6 %   RAM 83,4 MiB
tran rig (R5): 9.925 (khong subscriber) -> 5.951 (mosquitto_sub) -> 5.228 (gateway that)
```

**Nói thẳng phần không phải phép đo:** việc đường ống sẽ đạt N1 trên rig lớn hơn là **suy luận** từ
ba con số trần của R5 — 40 % phần hao thuộc fan-out của broker, 12 % thuộc gateway, và trên máy
nhiều core hơn hai thứ đó không còn tranh nhau. Suy luận này **chưa được đo**, và đó chính là việc
của M9.

Con số **4.999,665 msg/s** từng làm §F5 kết luận *"chỉ thiếu 0,007 %"* được tính bằng
`message gửi / thời lượng YÊU CẦU`. Sau khi `LoadRunner` dùng elapsed thật, khoảng cách là **6–11 %**.
Bài học giữ lại độc lập với quyết định này: **một phép đo chia cho thứ mình mong đợi thay vì thứ
mình quan sát được sẽ luôn sai về phía đẹp.**
