# ADR-024 — Một revision là một tài liệu bất biến; hệ thống giữ cả kệ, không giữ một bản

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-27 |
| **Liên quan** | `ADR-023` (claim), `ADR-001` (event store ở M5), [`AGENTS.md`](../../AGENTS.md) §4/K3, K4, [`scope.md`](../scope.md) §2.1, §6.5, [`plans/M1-factory-model-bus.md`](../plans/M1-factory-model-bus.md) §C07, §C08, [`audit-m0-m1.md`](../audit-m0-m1.md) R3 |

---

## Context

Plan C08 **bắt buộc** một phép kiểm: *"activate revision 3 khi đang ở 2 → thành công"*. Phép kiểm đó
chưa bao giờ tồn tại, và audit R3 tìm ra lý do sâu hơn *"quên viết test"*.

Cài đặt tới hết M1 nạp **một** `FactoryModelSnapshot` singleton từ **một** file
`deploy/seed/factory-model.json`, `"revision": 1`. Handler chặn ngay ở dòng đầu:

```csharp
if (_available.Revision != command.Revision) throw ...;
```

Nên revision 2 không tồn tại ở bất kỳ đâu trong hệ thống. Test *"activate revision 3 khi đang ở 2"*
không phải bị bỏ sót — nó **không viết được**. Hai hệ quả đo được:

- `EquipmentPathsRemoved` **chưa bao giờ khác rỗng** trong bất kỳ test nào. Một nửa nội dung của event
  demo M1 chưa từng chạy.
- **Staged rollout** — thứ `oef-mapping.md` đang tuyên bố và `glossary.md` đang định nghĩa là *"NV1
  chạy revision 12 trong khi DE1 còn ở 11"* — **bất khả thi** qua handler. Cả hai plant chỉ tới được
  revision 1.

**Ràng buộc nghiệp vụ đứng sau, và nó không nhỏ.** Nhà máy đổi liên tục: `FORM-01` lắp thêm kênh sạc
khi tăng công suất, `FORM-02` bị tháo khi vào đại tu, `L2` lắp thêm stacker. Trong khi đó hồ sơ
traceability của cell sản xuất **tháng trước** vẫn trỏ tới `NOVAVOLT/NV1/FORMATION/F1/FORM-02`. Câu
hỏi auditor sẽ hỏi (`scope.md` §6.5): *"Cell này chạy trên thiết bị nào, và lúc đó thiết bị đó thuộc
line nào?"* — chỉ tài liệu **đang có hiệu lực lúc đó** trả lời được. Ghi đè lên nó là mất câu trả lời
vĩnh viễn.

**Cái gì bị chặn nếu không quyết**: M2 có consumer cache `equipment_path` và dựa vào đúng hai field
added/removed để biết khi nào cây đổi. M10 phải trả lời *"cây trông thế nào ngày 12/3"*. Cả hai xây
trên một nền chưa từng được chứng minh.

## Decision

**Một revision là một tài liệu, và tài liệu không sửa tại chỗ.** Ba phần:

1. Seed thành một **kệ tài liệu**: `deploy/seed/factory-model.r1.json`, `.r2.json`, `.r3.json`. Publish
   một revision mới = **thêm một file**, không phải sửa file cũ.
2. `IFactoryModelCatalog` — mọi revision đang tồn tại, nạp một lần lúc dựng container, chỉ đọc. Tách
   hẳn khỏi `IActiveFactoryModel`, thứ nói **plant nào đang mở quyển nào**.
3. Handler tra tài liệu ứng viên **theo revision** trong catalog, rồi diff nó với tài liệu **đang có
   hiệu lực tại chính plant đó**.

Đây là K4 (append-only) áp cho master data. Không phải để cho gọn: nó là điều kiện để câu hỏi của
auditor có câu trả lời.

**Ranh giới**: catalog **chỉ đọc lúc chạy** — không có `Add`, không có reload. Không có gì trong hệ
thống đang chạy được phép tự nghĩ ra một revision. Và quyết định này **không** làm active revision bền
vững; nó vẫn nằm trong RAM và vẫn mất khi restart (M5).

## Consequences

**Được**

- Phép kiểm mà plan đòi từ C08 **chạy thật**: NV1 đi 1 → 2 → 3, và event của bước cuối báo đúng một
  path đến (`STACK-04`) và đúng một path đi (`FORM-02`).
- `EquipmentPathsRemoved` lần đầu tiên khác rỗng, tức là lần đầu tiên được chứng minh.
- **Staged rollout có thật**: NV1 ở revision 3 trong khi DE1 ở revision 1, cả hai qua handler.
- Diff tính so với **thứ đang có hiệu lực**, không phải so với tài liệu đứng cạnh trên kệ. DE1 bỏ qua
  revision 2 rồi nhảy thẳng 1 → 3 vẫn ra đúng một path mới, không kéo theo các kênh sạc của Hải Phòng.
- Bẫy nguy hiểm nhất của mô hình này bị chặn bằng máy: **tên file và số revision bên trong phải khớp**.
  Copy `r2` thành `r3` mà quên sửa số bên trong sẽ đưa cây cũ vào hiệu lực dưới một số revision mới —
  một thay đổi mà tất cả mọi người tin là đã xảy ra và không xảy ra.
- File có tên đúng dạng nhưng số không đọc được thì **dừng khởi động**, không bỏ qua im lặng. Một tài
  liệu bị bỏ qua là một đợt rollout biến mất, và không ai biết cho tới ca phải kích hoạt nó.
- Nền cho M10: *"cây trông thế nào ngày 12/3"* giờ chỉ còn thiếu trục thời gian, không thiếu dữ liệu.

**Mất / phải chịu**

- **Mọi revision nằm trong RAM.** Ở quy mô 41–46 node × 3 tài liệu thì không đáng kể; ở quy mô thật —
  `FORM-01` có ~1.000 kênh (`scope.md` §2.4) và hàng chục revision — thì không giữ được. M5 chuyển
  catalog vào SQL Server và nạp theo nhu cầu.
- **Không publish được revision lúc đang chạy.** Cố ý, nhưng nó có nghĩa là mọi đợt cập nhật cây đều
  cần một lần deploy cho tới khi M11 mang đường import thật vào.
- **Active revision vẫn mất khi restart.** Plant sống lại và tin rằng nó chưa chạy gì, nên lần activate
  kế tiếp sẽ báo **cả cây** là added. Có test ghim đúng giới hạn này thay vì để trong comment.
- Phép kiểm cũ *"file trên đĩa có còn đúng thứ anh đã đọc không"* biến mất. Nó được thay bằng một thứ
  mạnh hơn — tài liệu không bao giờ bị ghi lại — nhưng chỉ mạnh hơn **chừng nào không ai sửa tay một
  file đã publish**. Đó là lý do `deploy/seed/README.md` ghi thành luật và `FactoryModelSeedTests` đếm
  cứng số node của r1.
- Chữ ký đổi: `AddNvmFactoryModel` nhận **thư mục**, và `SeedFileLocator` thành `SeedDirectoryLocator`
  với biến môi trường `NVM_SEED_DIR`.

**Việc phát sinh**

- **M5**: catalog trong SQL Server; active revision bền vững; bỏ test *"mất khi restart"* và thay bằng
  test ngược lại.
- **M10**: effectivity theo thời gian trên chính catalog này — *"revision nào có hiệu lực lúc 14:20
  ngày 12/3"*.
- **M11**: B2MML thay **nguồn** của tài liệu, không thay hình dạng của nó.
- Khi số revision nhiều lên, cân nhắc nạp lười thay vì nạp hết lúc khởi động.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Giữ một file, thêm `Reload()` có kiểm soát** | Test phải **ghi đè file** để tạo revision 2 — tức là mô phỏng đúng thao tác mà C08.1 vừa cấm, và dạy sai ngay ở chỗ đang cố dạy đúng. Thêm nữa nó không giúp gì cho M10: reload cho biết cây *hiện tại*, không giữ cây *hôm qua* |
| **Thu hẹp tuyên bố của M1** (hướng A của audit) | Rẻ nhất, và để lại `EquipmentPathsAdded`/`Removed` chưa từng được chứng minh trong khi M2 sắp có consumer dựa vào đúng hai field đó. Trả nợ bằng cách xoá dòng ghi nợ |
| **Đưa catalog vào SQL Server ngay ở M1** | Cùng lý do đã loại ở `ADR-023`: M1 không có database, và kéo một cái lên chỉ để phục vụ master data sẽ đẻ ra migration, schema và seeding trước khi bố cục Functional Block chốt ở M5 |
| **Nhúng tài liệu làm `EmbeddedResource`** | Đổi cây thành đổi binary. Trái §C07 và trái mục tiêu *"file là thứ kỹ sư xem, sửa và diff được trong review"* |
| **Một file chứa mọi revision** | Sửa một revision là ghi lại file chứa tất cả — đúng thứ append-only cấm. Và diff trong git sẽ vô nghĩa |

## Evidence

Máy đo: Windows 11, .NET 10, cấu hình `Release`. HEAD `7934ac1`, cây làm việc R3 chưa commit.

**Trước**: `make ci` **292/292** xanh, và trong đó **không có test nào** activate revision khác 1.

**Sau**: `make ci` **308/308** xanh — thêm **16** test: 7 cho chuyển revision qua handler, 9 cho catalog.

Số đo của chính phép chuyển, lấy từ assertion đang chạy:

| Bước | NodeCount | Added | Removed |
|---|---|---|---|
| NV1 · lần đầu → r1 | 31 | 31 (cả cây) | 0 |
| NV1 · r1 → r2 | 35 | **4** (`FORM-01-CH-0005…0008`) | **0** |
| NV1 · r2 → r3 | 35 | **1** (`ASSEMBLY/L2/STACK-04`) | **1** (`FORMATION/F1/FORM-02`) |
| DE1 · r1 → r3 *(bỏ qua r2)* | 10 | **1** (`PACK/P1/EOL-01`) | **0** |

**Lab phá hoại** — thay `before` bằng tập rỗng, tức là hoàn nguyên đúng thế giới một-tài-liệu nơi
không có gì từng có hiệu lực:

```
total: 308 · failed: 3 · succeeded: 305
```

Ba test đỏ là đúng ba test diff, và cả ba đỏ với **cùng một triệu chứng**: toàn bộ cây báo là *added*.
Ví dụ ở bước r2 → r3, kỳ vọng `["…/L2/STACK-04"]`, thực tế **35** path — tức là mọi node của NV1. Đó
chính xác là hành vi mà một tài liệu duy nhất chỉ có thể sinh ra, và là bằng chứng rằng ba test này
nhìn thấy đúng thứ chúng sinh ra để nhìn.

**12 test cũ của activation vẫn xanh trong lab đó.** Cùng bài học với `ADR-023`: bộ test cũ không bỏ
sót lỗi này — nó **không thể** thấy, vì mọi test đều dừng ở lần activate đầu tiên.

**Hoàn nguyên**: handler khôi phục từ bản sao và đối chiếu `sha256sum -c` — khớp bit-for-bit. Không
dùng `git checkout` ([`AGENTS.md`](../../AGENTS.md) §1.2).
