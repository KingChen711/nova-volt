# ADR-048 — Retention là job tự viết đứng sau legal hold, không dùng retention policy của TimescaleDB

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-27 |
| **Liên quan** | `docs/scope.md` §8.4, §9/M12 · ADR-011, ADR-032, ADR-034 · migration ingestion 007, 010, 016 |

---

## Context

Scope §8.4: raw telemetry giữ 400 ngày, rollup 1 phút giữ 15 năm, và cờ legal hold chặn **mọi** retention. Migration
007 và 010 đã gỡ cả hai retention policy vì legal hold chưa có: bật phần xoá khi chưa có phần chặn là cho phép xoá hồ sơ
pháp lý mà không gì cản. `add_retention_policy` của TimescaleDB xoá mọi chunk quá hạn, không hỏi ai; không cấu hình được
điều kiện chặn. Thêm vào đó, ADR-011: đồng hồ máy sai đưa bản ghi **mới** vào chunk **cũ**.

## Decision

- Bảng `ts.legal_hold` (site, khoảng thời gian, lý do, người đặt). Trigger chỉ cho phép thả hold một lần; không xoá,
  không sửa khoảng hay lý do.
- Procedure `ts.enforce_retention` chạy bằng `add_job` mỗi ngày cho `ts.telemetry_measurement` (400 ngày),
  `ts.process_signal_1m` và `ts.process_signal_machine_1m` (15 năm). Nó duyệt từng chunk quá hạn và chỉ xoá chunk khi:
  không hold nào đang hiệu lực chồng lên khoảng của chunk; và với raw telemetry, chunk không chứa dòng nào có
  `recorded_at` trong 400 ngày gần đây. Mọi quyết định (`dropped`, `held`, `recently_recorded`) ghi vào `ts.retention_log`.
- Chunk chứa mọi site nên hold của một site giữ cả chunk (thận trọng, không xoá một phần chunk).
- Job đầu tiên chạy một giờ sau migration.

## Consequences

**Được**

- Không có đường xoá telemetry nào không đi qua hold; test khẳng định không còn `policy_retention`.
- Bản ghi mới nhận nhưng mang giờ máy sai không bị xoá theo giờ máy.
- Có nhật ký để trả lời "vì sao chunk này còn" và "job đã xoá gì".

**Mất / phải chịu**

- Tự bảo trì một procedure thay cho policy có sẵn; khi nâng TimescaleDB phải chạy lại test này.
- Hold một site giữ dữ liệu của mọi site trong cùng chunk ngày đó lâu hơn horizon.
- Một chunk có một dòng ghi muộn sẽ được giữ trọn cho tới khi dòng đó cũng quá 400 ngày.
- Chưa có API/Mendix để đặt/thả hold; hiện đặt bằng SQL với tài khoản migration. Người đặt hold là trường tự khai.
- `drop_chunks` trên hypertable có continuous aggregate để lại chunk "đã xoá" trong catalog; procedure bỏ qua chunk
  không còn bảng (`to_regclass`). Phát hiện khi test chạy job lần hai.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| `add_retention_policy` + kiểm tra hold ở ứng dụng trước khi bật/tắt policy | Policy chạy nền không hỏi; khoảng giữa kiểm tra và chạy là khe hở |
| Chỉ chặn hold trên raw, để rollup tự xoá | Sau ngày 400 rollup là bản ghi cuối (ADR-032, migration 010) |
| Xoá theo `recorded_at` thay `device_timestamp` | Chunk chia theo `device_timestamp` (ADR-011); xoá theo trục khác là xoá từng dòng, mất lợi thế chunk |

## Evidence

`LegalHoldRetentionTests` (TimescaleDB 2.29.2-pg17 Testcontainers):

- Raw: 4 dòng (500 ngày; 450 ngày có hold; giờ máy 600 ngày nhưng nhận hôm nay; 10 ngày). Lượt 1 → xoá đúng dòng
  500 ngày; nhật ký `recently_recorded, dropped, held:LH-RECALL-1`. Xoá hold hoặc sửa khoảng → lỗi; thả hold → lượt 2 xoá
  dòng 450 ngày; còn dòng giờ máy sai và dòng 10 ngày. 3 job `enforce_retention`, 0 `policy_retention`.
- Rollup: dòng 16 năm trước sau refresh cagg; hold giữ nó; thả hold → bị xoá; nhật ký `held, dropped`.
- Ổn định: sau khi dời lượt chạy đầu, hai lớp `LegalHoldRetentionTests` + `TelemetryPolicyTests` xanh 6/6 lần liên tiếp
  và `TelemetryPolicyTests` riêng xanh 4/4. Trước đó, khi job chạy ngay lúc tạo, gặp deadlock với lệnh gọi tay trong test
  và một lần `TelemetryPolicyTests` đỏ (chunk "already compressed"); thêm một lần đỏ ở
  `DroppingChunks_WouldDeleteAFreshRecord...` ngay sau khi dời. Nguyên nhân lần đỏ đó **chưa xác minh** (giả thuyết:
  job compression nền chạy cùng lúc với thao tác tay của test; chưa có phép đo phân biệt).
