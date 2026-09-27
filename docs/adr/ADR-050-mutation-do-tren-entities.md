# ADR-050 — Mutation score "domain layer" đo trên `Entities/` của mỗi FB

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-27 |
| **Liên quan** | ADR-049 (mục mutation), `docs/scope.md` §M13 DoD "Mutation score domain layer ≥ 70%", `tools/mutation/run-mutation.sh` |

---

## Context

Scope M13 đòi mutation score **domain layer** ≥ 70 %. Lần đo đầu (ADR-049) chạy Stryker.NET 5.0 trên **cả project** của năm
FB và được 12–36 %: phần lớn mutant là NoCoverage nằm trong `Handlers/`.

Mỗi FB có hai loại code:

- `Entities/`: aggregate, luật, value object, hash nội dung. Không phụ thuộc hạ tầng. Kiểm bằng unit test.
- `Handlers/`: command processor điều phối port (event store, store SQL, facet của FB khác). Kiểm bằng integration
  test với SQL Server/PostgreSQL/RabbitMQ thật (Testcontainers).

Stryker.NET chỉ chạy được test trong process: không dùng được Microsoft.Testing.Platform, không bật được mutant với xunit
v3 qua VSTest (ADR-049), và không chạy được integration test cần container. Muốn đo handler bằng mutation phải viết
thêm một bộ unit test dùng fake cho mọi port. Bộ đó sẽ lặp lại phần integration test đã kiểm bằng hạ tầng thật, và
chính fake lại là chỗ dễ sai.

## Decision

"Domain layer" trong DoD mutation là thư mục `Entities/` của mười FB (Equipment, Passport, Quality, Grading, Traceability,
Material, ProductionExecution, Recipe, MasterData, FactoryModel). `tools/mutation/run-mutation.sh` mặc định chạy với
`--mutate "**/Entities/**/*.cs"`, in điểm từng FB và điểm gộp. Ngưỡng 70 % áp cho **từng FB** và cho điểm gộp.
`Handlers/` không thuộc phạm vi này; `NVM_MUTATE="**/*.cs"` vẫn đo được cả FB khi cần xem.

## Consequences

**Được**

- Con số đo đúng thứ scope gọi là domain: luật nghiệp vụ, không phải code điều phối.
- Lượt đo làm lộ các nhánh luật chưa có test. Grading `GradingRuleSet` trước đó **không có unit test nào** (0 %). Cpk một
  phía, bảng người ký theo disposition, luật vật liệu, máy trạng thái formation cũng chưa có. Các test mới phủ những chỗ đó.

**Mất / phải chịu**

- Đây là định nghĩa do dự án tự chốt. Người đọc DoD có thể hiểu "domain" rộng hơn. Con số cả FB vẫn thấp (lần đo
  2026-09-27 trước khi thêm test: 12–36 %) và được ghi ở đây để không ai đọc 83 % thành "handler đã được mutation test".
- Handler chỉ được bảo vệ bằng integration test. Mutation không đo được độ chặt của các test đó.
- Mutant còn sống chủ yếu là chuỗi thông báo lỗi (`InvalidDataException("…")`): test kiểm loại exception, không kiểm
  câu chữ. Chấp nhận có chủ đích; câu chữ không phải hợp đồng.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Đo cả project FB, viết unit test với fake cho mọi port của handler | Nhân đôi integration test; fake SQL không bắt được lỗi transaction/lock thật, đúng loại lỗi handler hay mắc |
| Hạ ngưỡng xuống mức đang đạt | Đổi mục tiêu cho vừa kết quả |
| Bỏ mutation khỏi M13 | Mất tín hiệu duy nhất về độ chặt của unit test luật nghiệp vụ |

## Evidence

`tools/mutation/run-mutation.sh` (Stryker.NET 5.0, `tests/Mutation/Nvm.DomainMutationTests`, xunit v2 VSTest),
2026-09-27, Windows 11. Điểm = (killed + timeout) / (killed + timeout + survived + no coverage):

| FB | Trước (chỉ test cũ) | Sau |
|---|---:|---:|
| Equipment | 76,71 % | 76,71 % (56/73) |
| Passport | 76,56 % | 76,56 % (49/64) |
| Quality | 64,06 % | **96,88 %** (62/64) |
| Grading | 0,00 % | **94,92 %** (56/59) |
| Traceability | 73,47 % ¹ | 73,47 % (108/147) |
| Material | chưa đo | 100 % (28/28) |
| ProductionExecution | chưa đo | 100 % (14/14) |
| Recipe | chưa đo | 95,24 % (20/21) |
| MasterData | chưa đo | 80,00 % (4/5) |
| FactoryModel | chưa đo | 76,47 % (13/17) |
| **Gộp** | | **83,33 % (410/492)** |

¹ Lượt "trước" của Traceability chạy sau khi `ProductionUnitReplayTests` đã được thêm (Stryker build lại project test ở
mỗi lượt), nên hai cột bằng nhau. Không có số Traceability trước test mới trên `Entities/`.

Unit test toàn solution: 752/752 xanh (trước 669).

Giới hạn: một lần chạy; điểm gộp tính trên mutant được test (bỏ Ignored và CompileError như Stryker).
