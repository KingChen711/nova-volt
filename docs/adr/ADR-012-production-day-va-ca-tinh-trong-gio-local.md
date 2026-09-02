# ADR-012 — `production_day` và ca kíp tính trong giờ local của site

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-30 |
| **Liên quan** | `ADR-011` (ba loại timestamp), `ADR-020` (không `InvariantGlobalization`), [`scope.md`](../scope.md) §2.3, §9/M3, [`plans/M3-telemetry-timescaledb-production-calendar.md`](../plans/M3-telemetry-timescaledb-production-calendar.md) §C02, §C03 |

---

## Context

Nhà máy không đếm sản lượng theo ngày dương lịch. Nó đếm theo **chu kỳ sản xuất** bắt đầu lúc 06:00
giờ local và kết thúc lúc 06:00 hôm sau ([`scope.md`](../scope.md) §2.3). Ca C chạy 22:00 → 06:00, nên
**sáu giờ của nó nằm ở ngày dương lịch kế tiếp** nhưng cả ca thuộc `production_day` của ngày nó bắt
đầu. Người ký nhận sản lượng, ERP, và auditor đều dùng con số đó.

Hai ràng buộc thật, không phải giả định:

1. **`DE1` (`Europe/Berlin`) đổi giờ hai lần mỗi năm.** [`FactorySite.cs`](../../src/FunctionalBlocks/FactoryModel/Entities/FactorySite.cs)
   đã mang `TimeZoneId` IANA từ M1 và XML doc của nó đã viết sẵn nguyên cớ. Ngày **29/03/2026** đồng hồ
   nhảy 02:00 → 03:00; ngày **25/10/2026** đồng hồ lùi 03:00 → 02:00. Ca C vắt qua cả hai.
2. **`NV1` (`Asia/Ho_Chi_Minh`) là UTC+7 cố định.** Nghĩa là mọi cách tính sai đều **đúng ở `NV1`**
   365 ngày một năm. Một site không chứng minh được gì cho site kia.

Cái gì bị chặn nếu không quyết: mọi thứ đếm theo ngày hoặc theo ca — yield ngày (M6), OEE (M6),
màn hình *"ca này đã làm được bao nhiêu"* (M4), và câu hỏi truy vết *"lô này sản xuất ngày nào"*.

Điều đã biết lúc quyết: bảng ca của NovaVolt là ba ca 8 giờ; M10 sẽ có site dùng bảng khác.

## Decision

**`production_day` là ngày dương lịch — trong giờ local của site — mà ca mở đầu của chu kỳ đó bắt
đầu.** Cách tính: đổi instant sang giờ local bằng `TimeZoneInfo` của site, **rồi mới** so với bảng ca
và đọc ngày. Không trừ 6 giờ trên UTC, không dùng offset chuẩn của site như một hằng số.

Ba hệ quả được chốt cùng lúc:

- **`ProductionDay` là kiểu riêng**, không phải `DateOnly`, và **không có phép chuyển đổi** theo cả hai
  chiều. Hai kiểu chứa cùng ba con số — đó chính là lý do: có conversion thì `CAST(device_timestamp AS date)`
  gán được vào một `production_day` mà compiler vẫn đồng ý.
- **Bảng ca là dữ liệu** (`ShiftSchedule`), kiểm lúc dựng là phủ **đúng 24 giờ** không hở không chồng.
  Không `switch-case` trên `Shift`.
- **Ranh giới ca trả về hai instant tuyệt đối**, nửa mở `[start, end)`. "22:00 tới 06:00" là câu trên
  bảng phân ca, không phải thứ dùng để lọc row.

**Quy tắc cho hai giờ bất thường mỗi năm** — phát biểu **một lần** cho cả ba trường hợp:

> Instant của một số đọc đồng hồ là **instant sớm nhất mà đồng hồ local đọc ra ≥ số đó**.

| Trường hợp | Kết quả |
|---|---|
| Ngày thường | Đúng instant có số đọc đó |
| Giờ **lặp lại** (mùa thu) | **Lần đầu** trong hai lần. Ca C bắt đầu ở 22:00 đầu tiên, giờ dư nằm **trong** ca → ca dài **9 giờ** |
| Giờ **không tồn tại** (mùa xuân) | Đúng lúc đồng hồ nhảy qua nó. Ca C mất một giờ → dài **7 giờ** |

Một quy tắc cho cả ba là thứ giữ cho `GetShift` và `GetShiftBoundaries` **không mâu thuẫn nhau**: một
instant nằm trong ranh giới của ca X **khi và chỉ khi** `GetShift` trả về X, và ca này kết thúc đúng
chỗ ca sau bắt đầu.

**Không thuộc quyết định này**: bảng ca theo site (M10 — hôm nay mọi site dùng `ShiftSchedule.Default`),
lịch nghỉ lễ, ca gãy (split shift), và mọi phép tính nghiệp vụ trên `production_day` (M6).

## Consequences

**Được**

- Ca C của `DE1` dài **7 giờ** và **9 giờ** đúng hai ngày trong năm, đo được, không phải một trường hợp
  đặc biệt viết tay.
- `production_day` của 05:59 và 06:01 lệch **đúng một ngày** ở **cả hai** site (D4).
- Kiểu riêng chặn được đường sai phổ biến nhất ngay ở compiler, không đợi tới báo cáo cuối tháng.
- Bảng ca thay được cho M10 mà không sửa dòng logic nào.

**Mất / phải chịu**

- **Phụ thuộc vào dữ liệu múi giờ của runtime.** `ADR-020` đã cấm `InvariantGlobalization`; nếu ai đó
  bật lại để giảm kích thước container thì mọi ID IANA hỏng. Chống bằng test khẳng định cả hai ID tra
  được, và `SiteTimeZone.Of` đổi thông báo lỗi thành một câu nêu đích danh `ADR-020`.
- **`GetShiftBoundaries` đắt hơn một phép cộng.** Trường hợp giờ không tồn tại phải tìm mốc chuyển bằng
  chia đôi (~46 vòng `ConvertTimeFromUtc`). Chỉ chạy khi ranh giới rơi đúng vào giờ đó — bảng ca của
  NovaVolt không bao giờ chạm — nhưng cái giá là thật và được ghi ở đây thay vì để ai đó phát hiện lúc
  profiler chỉ vào nó.
- **Quy tắc "lần đầu" cho giờ lặp lại là một lựa chọn**, không phải một sự thật. Nó đúng cho *ranh giới
  ca*; một milestone sau cần ngữ nghĩa khác (ví dụ: mốc hết hạn của một lot) phải nêu ra thay vì mượn
  quy tắc này.
- Chuyển đổi tường minh `ProductionDay.Date` xuất hiện ở mọi chỗ ghi xuống DB. Đó là chủ đích, và nó
  làm code dài hơn.

**Việc phát sinh**

- Múi giờ phải đọc từ factory model đang có hiệu lực, không từ bảng tra thứ hai (**C04**).
- Lab phá hoại đo sai số của `CAST(device_timestamp AS date)` trên dữ liệu thật (**C14**), số vào
  §Evidence dưới đây.
- M4 là hộ dùng thật đầu tiên. Nếu `IProductionCalendar` thiếu phép nào thì phát hiện ở đó.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Trừ 6 giờ trên UTC rồi lấy ngày | Sai ở `DE1` hai ngày mỗi năm, **đúng** ở `NV1` mọi ngày. Đo được: 8/564 test đỏ, xem §Evidence. Hai ngày liền kề cùng sai, ngày này thừa đúng bằng ngày kia thiếu, nên tổng tháng vẫn khớp |
| Áp `BaseUtcOffset` của site như hằng số | Cùng lỗi, ẩn hơn: trông như "đã quan tâm tới múi giờ" nhưng bỏ qua DST. Đây chính là bản đã dựng lại để đo |
| `production_day` là `DateOnly` | Không có gì ngăn gán `CAST(ts AS date)` vào nó. Sai lệch chỉ lộ ở báo cáo cuối tháng |
| Lưu `production_day` cùng row lúc ingest | Đóng băng một quyết định nghiệp vụ vào dữ liệu append-only. Đổi bảng ca (M10) là phải viết lại lịch sử — thứ K4 cấm. Tính lúc đọc thì đổi bảng ca chỉ đổi câu trả lời của tương lai |
| Để `TimeZoneInfo` tự quyết giờ lặp lại | Mặc định của thư viện là *"giờ chuẩn"*, tức lần **thứ hai**. Chọn thế thì ca C dài 8 giờ vào ngày 25/10 và một giờ sản xuất biến mất khỏi mọi báo cáo — không exception, không log |

## Evidence

Đo ngày 2026-08-30, commit `26c752b` (C02) + cây làm việc C03. Lệnh:
`dotnet test --project tests/Unit/Nvm.UnitTests/Nvm.UnitTests.csproj`.

**Độ dài thật của ca C — bốn tổ hợp** (2 ngày × 2 site), từ `ProductionCalendarDaylightSavingTests`:

| Site | `production_day` | Ranh giới thật | Độ dài |
|---|---|---|---|
| `DE1` | 2026-03-28 | `2026-03-28T22:00+01:00` → `2026-03-29T06:00+02:00` | **7 giờ** |
| `DE1` | 2026-10-24 | `2026-10-24T22:00+02:00` → `2026-10-25T06:00+01:00` | **9 giờ** |
| `NV1` | 2026-03-28 | `2026-03-28T22:00+07:00` → `2026-03-29T06:00+07:00` | **8 giờ** |
| `NV1` | 2026-10-24 | `2026-10-24T22:00+07:00` → `2026-10-25T06:00+07:00` | **8 giờ** |

Độ dài **cả production day** ở `DE1`: **23 giờ** (28/03) và **25 giờ** (24/10). Ba ca vẫn khớp nhau
không hở không chồng ở cả hai ngày.

**Lab phá hoại — hoàn nguyên về cách tính sai.** Thay `ProductionCalendar` bằng bản dùng
`BaseUtcOffset` + trừ `DayStart` trên UTC, giữ nguyên toàn bộ test:

- **8 / 564 test đỏ.** Bảy trong `ProductionCalendarDaylightSavingTests`, một là `D4` ở `DE1`.
- Cả **hai** test `D4` ở `NV1` vẫn xanh, và test đối chứng `NV1` của D3 cũng vẫn xanh — đúng như dự
  đoán: site không DST không phát hiện được lỗi này.
- Thông báo đỏ tiêu biểu: `boundaries.Duration should be 07:00:00 but was 08:00:00`.

**Ba test lúc đầu KHÔNG bắt được lỗi**, và đây là phần đáng giá nhất của lab: bản sai cho ba ca
**8 giờ cố định**, nên chúng vẫn *khớp nhau* hoàn hảo và vẫn *chứa* các instant được hỏi. Phải thêm
hai loại khẳng định mới bắt được:

1. **tổng độ dài production day** (23 / 25 giờ) — chứ không chỉ "ba ca khớp nhau";
2. **giờ đầu và giờ cuối của ca** — chứ không chỉ một instant ở giữa.

Đây là cùng một bài học M2/C17 đã ghi: một phép kiểm không bao giờ đỏ là một phép kiểm chưa kiểm gì.
Bản gốc của lab: `ProductionCalendar.naive.cs`, dựng lại được từ mục Alternatives ở trên.

**Lab C14 — `CAST(device_timestamp AS date)` vẫn trả lời hợp lệ nhưng gán sai ngày.** Đo ngày
2026-08-31 bằng `make calendar-lab`. Lab lấy timezone từ factory model revision 3, đặt timezone của
PostgreSQL session về đúng timezone site, rồi chạy đúng phép `CAST(... AS date)`. Việc đặt tường minh
là quan trọng: cast một `timestamptz` phụ thuộc cấu hình session; để session ở UTC có thể vô tình che
bớt lỗi tại `NV1`, chứ không biến ngày dương lịch thành `production_day`.

| Nguồn | Site / cửa sổ | Row | Gán sai | Tỉ lệ |
|---|---|---:|---:|---:|
| **Thật** — fixture C10 | `NV1`, `Formation/Temperature`, `[2026-07-20, 2026-07-27)`, 100 kênh | **121.429** | **30.334** | **24,980853 %** |
| **Dựng bằng code lịch** — ca C ngày thường | `DE1`, `production_day=2026-02-14`, mỗi phút trôi qua một row | **480** | **360** | **75,000000 %** |
| **Dựng bằng code lịch** — ca C DST mùa xuân | `DE1`, `production_day=2026-03-28`, ca thật 7 giờ | **420** | **300** | **71,428571 %** |
| **Dựng bằng code lịch** — ca C DST mùa thu | `DE1`, `production_day=2026-10-24`, ca thật 9 giờ | **540** | **420** | **77,777778 %** |

So với ca C ngày thường, hai ngày DST làm tỉ lệ đổi **−3,571429** và **+2,777778 điểm phần trăm**.
Không row nào hỏng định dạng và PostgreSQL không ném lỗi; phép sai chỉ chuyển số lượng giữa hai ngày
liền kề. Target khóa fingerprint thật ở **121.429 row / 100 kênh / toàn bộ `clock_quality=Good` /
đúng timestamp đầu-cuối**, đồng thời từ chối một phép kiểm rỗng. Ba unit test riêng khóa oracle ở hai
phía nửa đêm và trường hợp không có row. Số chi tiết cũng nằm ở [`benchmarks.md`](../benchmarks.md) §M3.
