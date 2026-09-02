# ADR-035 — Readiness của một export nằm trong CHÍNH TÊN nó, không nằm ở file marker bên cạnh

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-09-02 |
| **Liên quan** | `ADR-033` §Quyết định điểm 10, `docs/plans/M3-telemetry-timescaledb-production-calendar.md` C15, `docs/glossary.md` §8, `docs/scope.md` §8.3, `docs/runbook.md` §2 |

---

## Context

`ADR-033` điểm 10 nói adapter *"atomically rename file khỏi public inbox vào `.processing`, đọc bytes
đúng một lần"*. Câu đó đúng về phía **đọc** và không nói gì về phía **ghi**. Audit 2026-09-02 chỉ vào
chỗ trống đó, và bản sửa **đầu tiên** cho chỗ trống đó — một file marker `<tên>.csv.ready` nằm cạnh
file dữ liệu — bị **chính probe của vòng audit sau bác bỏ**. ADR này ghi cả hai.

Ràng buộc thật:

1. **Rename không đóng handle của người khác.** Trên Linux/NFS, exporter đang mở `foo.csv` vẫn ghi
   tiếp vào cùng inode sau khi ingestion đã rename file sang `.processing`. Ingestion đọc phần đầu,
   lưu vào DB, rồi xoá snapshot — phần đuôi biến mất cùng claim. **Không có exception ở đâu cả**.
2. **`SettleTime` là phỏng đoán.** Nó hỏi mtime đã im được 2 giây chưa; một exporter ngưng 3 giây
   giữa chừng trả lời "xong" cho một file chưa xong. Ngưỡng lớn hơn chỉ đổi xác suất.
3. **Chỉ người ghi biết file đã xong.** Không có primitive nào cho phép bên đọc hỏi đáng tin qua NFS
   *"file này còn ai đang mở không"*; `flock` là advisory.
4. **Filesystem không có rename nguyên tử cho nhiều file.** Đây là ràng buộc giết bản sửa đầu tiên,
   và là lý do quyết định cuối cùng chỉ có **một** file.
5. **POSIX `rename` luôn ghi đè.** Bên đọc **không thể** giữ chỗ một cái tên trước một producer:
   `mv` của producer đi xuyên qua mọi placeholder, kể cả file tạo bằng `O_CREAT|O_EXCL`.

Cái bị chặn nếu không quyết: C15 là đường ghi thật vào TimescaleDB và MinIO WORM. Một file đọc sớm
tạo ra **telemetry của nửa run** và một **bản gốc WORM cũng chỉ có nửa run** — cả hai bất biến, cả
hai sai, và K4 vẫn báo xanh vì bản gốc *có tồn tại*.

## Decision

**Một export là một file, và tên của nó là lời tuyên bố nó đã xong.**

1. Hợp đồng cho producer: ghi export ra tên tạm (`<tên>.csv.partial`) → **đóng file** → **rename
   nguyên tử** thành `<tên>.csv.ready`. Phép rename đó **là** publish. Trước nó, file thuộc về
   exporter; sau nó, file đã xong.
2. Adapter chỉ đọc `*.csv.ready`, và claim bằng **đúng một** `File.Move` sang `.processing/<guid>/`,
   đồng thời bỏ hậu tố (`foo.csv.ready` → `foo.csv`). Lấy file **là** lấy readiness, vì chúng là một.
3. **Khôi phục không bao giờ nhắm vào một cái tên producer có thể dùng.** File trả về inbox (retry
   sau lỗi, hoặc claim mồ côi sau crash) luôn mang tên mới `<stem>.retry-<guid>.csv.ready` /
   `<stem>.recovered-<guid>.csv.ready`, bằng **một** `File.Move`. Không giữ chỗ, không placeholder,
   không ghi đè — không có gì để mất và không mất gì của ai.
4. `PublishedSuffix` để rỗng thì tắt hợp đồng và quay về `SettleTime`; watcher **nói ra** điều đó một
   lần lúc khởi động (EventId **2513**, Warning).
5. Vì hợp đồng fail-closed, mọi file nằm trong inbox quá `UnpublishedWarningAfter` (mặc định 5 phút)
   mà không mang tên đã publish được log **một lần** (EventId **2514**).

Ranh giới: quyết định này **không** nói gì về nội dung file (vẫn là `CsvMeasurementReader` và các
luật từ chối của `ADR-033`), và **không** bảo vệ được trước một producer ghi thẳng vào
`<tên>.csv.ready` — xem §Consequences.

## Consequences

**Được**

- Claim là **một syscall**. Không có khoảnh khắc nào giữa "lấy readiness" và "lấy dữ liệu" để một
  producer chen vào, vì không có hai bước.
- Khôi phục là **một syscall**, nhắm vào một tên GUID. Không cửa sổ ghi đè, không placeholder cần
  bảo vệ, và không trạng thái nửa vời nào để crash dừng lại trong đó.
- Không còn khái niệm marker mồ côi, không cần quét inbox lúc khởi động, không có file nào có thể bị
  kẹt trong inbox vì mất marker.
- Ít code hơn hẳn bản marker: bỏ `TakeMarker`, `ReturnMarker`, `PublishMarker`, `ReserveInboxPath`,
  `SweepOrphanedMarkers`, và hai nhánh restore gộp thành một.

**Mất / phải chịu**

- **Mọi exporter phải sửa**, và sửa theo cách khác lần trước: đổi tên đích thành `*.csv.ready` thay
  vì tạo thêm một file. Không làm gì cả thì file **không bao giờ được đọc** (fail-closed), và đó là
  lý do §Decision điểm 5 tồn tại.
- **Producer ghi thẳng vào `<tên>.csv.ready` vẫn phá được hợp đồng.** Bên đọc không phân biệt được
  "đã rename" với "đang ghi thẳng vào tên đích". Hợp đồng chỉ mạnh bằng phép rename của producer;
  thứ nó bảo đảm là một producer **chưa được dạy** thì fail-closed chứ không lặng lẽ hỏng dữ liệu.
- Operator nhìn inbox thấy `run-123.csv.ready` thay vì `run-123.csv`. File `processed/` và `rejected/`
  vẫn mang tên `run-123.csv` vì hậu tố bị bỏ lúc claim.
- File khôi phục **không giữ tên cũ**. Đây là chủ ý (điểm 3) và là đánh đổi thật: operator theo dõi
  một export qua nhiều vòng retry phải theo nội dung/`archive_id`, không theo tên.
- Vẫn còn cửa sổ **at-least-once** cũ: crash giữa lúc ghi bản `processed/` và lúc xoá file trong
  `.processing` làm export đó được xử lý lại sau khi khởi động. Dedup nuốt, archive content-addressed
  nên object không nhân đôi. Đây là ranh giới cố ý của cả adapter, không phải khiếm khuyết mới.
- Số D1 *"file chạm inbox → cả hai consumer nhận xong"* (**7.558 ms**, gồm 5.000 ms poll + 2.000 ms
  settle) đo ở chế độ cũ; phần settle không còn trong hiệu số. **Chưa đo lại.**

**Việc phát sinh**

- `scripts/bus-lab.sh` publish theo hợp đồng (`.partial` → `mv` thành `.ready`).
- `docker-compose.yml` khai báo `NVM_INGEST__FileDrop__PublishedSuffix` **tường minh**, để hệ thống
  đang chạy tự nói ra hợp đồng của nó (`AGENTS.md` §3.6, bài học 1).
- Hai probe `PAIR_RACE`/`RESERVATION_RACE` đã được dựng lại thành `scripts/file-drop-race-probe.sh`
  (`make file-drop-race-probe`) và **đã chạy trên image mang bản sửa** — xem §Evidence.
- Đo lại D1 fan-out ở chế độ mới khi chạy `make bus-fanout` lần tới.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Marker rời `<tên>.csv.ready` nằm cạnh `<tên>.csv`** (bản sửa đầu, đã viết và đã bỏ) | Hai file thì phải claim bằng hai lần rename. Probe `PAIR_RACE` trên image thật: lấy marker của A xong, producer publish B cùng tên, adapter lấy **data B dưới marker A** và để lại marker của B mồ côi trong inbox — `claimed_data=B inbox_marker=present`. Đảo thứ tự chỉ đổi nạn nhân: lấy data trước thì marker vừa tạo của B bị xoá và B kẹt lại vĩnh viễn. **Không có thứ tự nào đúng**, vì filesystem không rename nguyên tử hai file |
| Giữ chỗ tên bằng `FileMode.CreateNew` rồi ghi đè lên chính placeholder của mình | Probe `RESERVATION_RACE`: placeholder được tạo, handle đóng lại trước khi restore ghi, `mv` của producer thay placeholder bằng B, restore ghi đè B bằng A — `final_data=A`. `O_CREAT\|O_EXCL` là **nguyên tử lúc tạo**, không phải một cái khoá giữ được qua thời gian |
| Chỉ tăng `SettleTime` (5 s, 30 s) | Đổi xác suất, không đổi bản chất, và làm mọi file chậm thêm |
| Chỉ yêu cầu `.partial` → rename thành `<tên>.csv`, không có hậu tố riêng | Đúng và **không kiểm được**: `*.csv` không phân biệt "đã rename" với "đang ghi thẳng". Mất hẳn tính fail-closed — một exporter chưa được dạy sẽ hỏng dữ liệu **lặng lẽ** thay vì dừng lại ồn ào |
| Publish bằng **thư mục** (`<tên>.export/` chứa `data.csv`, rename thư mục vào inbox) | Cũng nguyên tử một bước và cũng đủ. Loại vì đắt hơn cho producer mà **không** đóng thêm được lỗ nào so với một file: producer ghi thẳng vào thư mục đã publish vẫn phá được y hệt. Ghi lại ở đây vì nếu sau này một export gồm nhiều file (CSV + ảnh + log), đây là chỗ mở lại |
| Mở file bằng `FileShare.None` để dò xem còn ai giữ handle | Trên Windows/SMB được; trên Linux/NFS `flock` là advisory nên phép thử luôn nói "rảnh". Một phép kiểm chỉ đúng trên nửa số nền tảng còn tệ hơn không có |
| Marker chứa size/hash để **phát hiện** cặp lệch thay vì ngăn nó | Đóng được `PAIR_RACE` bằng cách bắt lỗi sau khi đã lệch, nhưng bắt producer tính hash — đúng thứ khiến exporter cũ không tuân thủ nổi. Một file thì không có cặp nào để lệch |
| Để mặc định tắt hợp đồng, ai cần thì bật | "Đóng P1 bằng một tuỳ chọn mặc định tắt" là không đóng |

## Evidence

**Hai probe của vòng audit, trên image thật**, là bằng chứng bác bỏ phương án marker rời:

```
PAIR_RACE claimed_data=B inbox_marker=present
RESERVATION_RACE final_data=A
```

Dòng đầu: consumer lấy data của export B dưới readiness của export A, và marker của B ở lại inbox
làm mồ côi. Dòng sau: restore ghi đè export B mà producer vừa publish, bằng snapshot A — placeholder
`CreateNew` không cản được `mv`.

**Regression trong `tests/Integration/Nvm.IntegrationTests/FileDropTests.cs`** — **16/16 xanh**
(trước loạt sửa này: 11), chạy 2026-09-02 ở cả Debug và Release. Năm test mới:

| Test | Ghim điều gì |
|---|---|
| `AnExportNotYetPublished_IsNotClaimedAndNothingInItIsStored` | `foo.csv` chưa rename: ném `FileDropNotPublishedException`, DB **0 row**, archive **0 call**, `processed`/`rejected` rỗng, file còn nguyên trong inbox, `.processing` rỗng. Rename vào chỗ xong thì đúng file đó vào **2 row** |
| `ClaimingAnExport_TakesItsReadinessWithItAndLeavesNothingInTheInbox` | Sau claim, inbox **rỗng hoàn toàn** — không còn mẩu readiness nào để export sau thừa hưởng |
| `RestoringAClaimedExport_TakesANameNoProducerWouldPublish` | Ingest lỗi sau khi đọc; B đã publish đúng tên đó trong lúc bị chặn. B nguyên byte, A quay về dưới `published-twice.retry-<guid>.csv.ready` |
| `RecoveringAClaimAbandonedByACrash_KeepsBothExports` | Claim mồ côi + inbox đã có export mới cùng tên. **Cả hai** còn sống; A về dưới `crashed-mid-claim.recovered-<guid>.csv.ready` và đọc lại được **2 row** |
| `AClaimCancelledBeforeItIsRead_ComesBackPublishedRatherThanStayingClaimed` | Token đã cancel trước khi đọc: `OperationCanceledException` đi ra nguyên vẹn, file về inbox **đã publish**, `.processing` rỗng |

**Probe chạy lại trên image mang bản sửa**, `scripts/file-drop-race-probe.sh` (bản dựng lại, không
phải script gốc của auditor), container tạo `2026-09-02T01:32:28Z`, log khởi động EventId **2512**
xác nhận hợp đồng đang chạy chứ không chỉ nằm trong code:

```
PAIR_RACE claimed_data=AB torn_archives=0 inbox_leftover=none
RESERVATION_RACE final_data=AB
PROBE OK — moi oracle xanh
```

So với bản gốc trên thiết kế marker rời — `inbox_marker=present` → **`inbox_leftover=none`**, và
`final_data=A` (mất B) → **`final_data=AB`**.

Điều kiện đo, vì một probe không nói điều kiện thì không đọc lại được:

| | `PAIR_RACE` | `RESERVATION_RACE` |
|---|---|---|
| Kịch bản | producer publish lại **cùng một tên** liên tục 30 giây (**7.153–7.330 lần**), xen kẽ hai nội dung A/B | MinIO tắt ⇒ archive không tới được ⇒ claim bị trả về inbox; B publish vào đúng tên A đã đến |
| Race có mở ra không | **7 lần claim** rơi vào giữa cửa sổ publish (probe đỏ nếu < 2) | A quay ít nhất một vòng retry **trước khi** B được publish |
| Oracle | inbox rỗng sau khi rút hết · `.processing` rỗng · mọi bản gốc đã archive khớp exact byte A hoặc B · mỗi export trong DB đủ **20/20** row hoặc chưa vào, không bao giờ ở giữa | lấy mẫu custody **mỗi giây trong 15 giây** (30 lượt quan sát file) — A và B đều phải xuất hiện; rồi bật MinIO lại: **cả hai** tới `processed/` và **20/20** row |
| Negative control | file thả đúng nội dung nhưng **không** rename vào `.ready`: **0 row**, không bị claim, nằm nguyên trong inbox — rename vào chỗ thì **20 row** ngay | cùng control |

**Probe tìm ra hai lỗi mà 679 test xanh không thấy.** Cả hai nằm ngoài hai race nó đi tìm, và cả hai
đều đủ để làm hỏng đường file drop:

1. **Interval của một export không lưu được.** `Describe` kết thúc interval bằng `last.AddTicks(1)` —
   100 ns — trong khi `curve_start_at`/`curve_end_at` là `timestamptz`, tức **micro-giây**. Hệ quả
   một: export **một dòng** có interval bằng 0 sau khi lưu và bị chính check constraint
   `ck_raw_curve_archive_interval` từ chối, nên **cái export đời thường nhất của một máy EOL không
   bao giờ archive được** và file quay vòng retry vô hạn. Hệ quả hai: mọi lần archive **lại** cùng
   một export so descriptor (còn 100 ns) với row đọc về (đã bị cắt) và ném *"different plant
   evidence"* về đúng bằng chứng giống hệt nhau — **35 lần** trong một lượt probe. Nay
   `RawCurveDescriptor` làm tròn cả hai đầu về micro-giây **trước** khi kiểm, và `Describe` bước một
   micro-giây. Test cũ `TheOriginalBytes_AreArchivedBeforeTheFileIsFiledAsProcessed` từng **ghim
   nguyên cái tick đó** bằng một assertion; nó nay ghim micro-giây.
2. **Tên file retry dài thêm mỗi vòng.** `UniqueName` gắn marker vào *tên nó nhận được*, nên mỗi
   vòng retry dài thêm ~40 byte; qua sáu vòng thì vượt giới hạn 255 byte, `File.Move` hỏng, và
   export **kẹt lại trong `.processing`** — đúng cái nhánh mà `N-M3-29` vừa nói là đã đóng. Probe
   bắt được **5 claim kẹt** và những cái tên như
   `pair-race.retry-<guid>.retry-<guid>.retry-<guid>.retry-<guid>.retry-<guid>.retry-<guid>.csv`.
   Nay marker **thay** marker cũ và mang theo số vòng: `retry-` → `retry2-` → `retry3-`.

Hai lỗi này là lỗi **của bản sửa vòng 7**, không phải của vòng trước, và chúng là lý do §Evidence
này không còn nói *"đóng bằng thiết kế"* cho hai P1 mà nói *"đóng, và probe đã chạy lại xanh"*.

**Regression trong `tests/Integration/`** — `FileDropTests` **18/18**, `RawCurveArchiveTests` **7/7**,
unit **579/579**, chạy 2026-09-02 ở cả Debug và Release:

| Test | Ghim điều gì |
|---|---|
| `AnExportNotYetPublished_IsNotClaimedAndNothingInItIsStored` | `foo.csv` chưa rename: ném `FileDropNotPublishedException`, DB **0 row**, archive **0 call**, file còn nguyên, `.processing` rỗng. Rename vào chỗ thì vào **2 row** |
| `ClaimingAnExport_TakesItsReadinessWithItAndLeavesNothingInTheInbox` | Sau claim, inbox **rỗng hoàn toàn** — không còn mẩu readiness nào để export sau thừa hưởng |
| `RestoringAClaimedExport_TakesANameNoProducerWouldPublish` | B publish đúng tên đó trong lúc A bị chặn: B nguyên byte, A về dưới `published-twice.retry-<guid>.csv.ready` |
| `RecoveringAClaimAbandonedByACrash_KeepsBothExports` | Claim mồ côi + export mới cùng tên: **cả hai** còn sống, A đọc lại được **2 row** |
| `AClaimCancelledBeforeItIsRead_ComesBackPublishedRatherThanStayingClaimed` | `OperationCanceledException` đi ra nguyên vẹn, file về inbox đã publish, `.processing` rỗng |
| `AnExportOfOneReading_ReachesProcessedLikeAnyOther` | Export **một dòng** vào được `processed/`, và interval của nó không mang gì dưới micro-giây |
| `AnExportThatKeepsFailing_CountsItsRoundsInsteadOfGrowingItsName` | Ba vòng hỏng liên tiếp: `retry-` → `retry2-` → `retry3-`, độ dài tên **không đổi**, `.processing` rỗng |
| `ACurveOfOneInstant_IsArchivedAndStaysIdempotent` | Interval ngắn nhất lưu được đi hết đường MinIO + PostgreSQL, và archive **lần hai** vẫn idempotent (1 object, 1 row) |
| `AnIntervalThatOnlyExistsBelowAMicrosecond_IsRefusedWhereItIsBuilt` | Luật nằm ở `RawCurveDescriptor`, nên caller sau không tái tạo lại được lỗi |

**Chứng minh test có thể đỏ** (`AGENTS.md` §3.6, bài học 2). Chạy trên bản trước khi sửa:

```
RawCurveArchiveTests.AnIntervalThatOnlyExistsBelowAMicrosecond_IsRefusedWhereItIsBuilt [FAIL]
  Shouldly.ShouldAssertException : `new RawCurveDescriptor(..., instant, instant.AddTicks(1))`
FileDropTests.AnExportOfOneReading_ReachesProcessedLikeAnyOther [FAIL]
  Shouldly.ShouldAssertException : archived.Descriptor.CurveEndAt.Ticks % TimeSpan.TicksPerMicrosecond
```

**Nói thẳng chỗ vẫn chưa có bằng chứng:**

1. Test ghim **kết quả** ("khôi phục không bao giờ thay thế file đang ở tên đó", "claim không để lại
   gì"), không tái hiện **cửa sổ tranh chấp** ở mức một lệnh — cửa sổ đó chỉ điều khiển được nếu cấy
   seam vào production code. Probe đóng khoảng trống này bằng cách chạy race thật hàng nghìn lần,
   nhưng một probe xanh vẫn là *"không tái hiện được trong điều kiện này"*, không phải *"không thể
   xảy ra"*. Lập luận cấu trúc — claim một rename, khôi phục một rename vào tên GUID — mới là thứ
   nói *"không thể"*, và nó là lập luận.
2. Chưa có soak nhiều phút với producer và consumer song song ở tải thật. Thuộc **M13**, cùng soak
   24 giờ của `ADR-034`.
3. Export hỏng vĩnh viễn vẫn quay vòng retry **không có trần**. Đó là lựa chọn của `ADR-033` (chặn
   còn hơn bỏ), và nay số vòng đọc được ngay trên tên file; một chính sách poison-file là quyết định
   riêng, chưa làm.
