# ADR-057 — Master data bằng suite kiểu Tester Tool, WO theo ca ở extension app, quyền gọi command theo role

| | |
|---|---|
| **Status** | **Accepted** — agent quyết theo AGENTS.md §2.3, chờ chủ repo đọc |
| **Date** | 2026-10-28 |
| **Liên quan** | ADR-055 · ADR-056 · scope §5 (R4) · [bảng cơ chế](../oef-mapping.md#r4-solution-nv1) |

## Context

R4 dựng lớp đầu tiên của team trên lớp Product, và cách dự án nạp master data:

- Master data nạp bằng suite Tester Tool kèm CSV. Script deploy duyệt app theo `masterDataOrder`, trong mỗi app theo tên
  file, bỏ qua suite có tên trong file lịch sử. Script không đếm dữ liệu: trên máy ảo, suite lỗi vẫn vào lịch sử, lần
  deploy sau bỏ qua đúng suite cần chạy (L07). Tester Tool trả dòng đầu khi tham số đọc từ cột CSV không có, suite vẫn
  xanh (L16).
- WO theo ca: timer `DAILY_WO` của từng PS tự hoàn tất WO Frozen đủ `MinimumFreezeHours` của BoP, rồi đóng băng WO
  Active/Pause đã quá giờ kết thúc, kích hoạt WO New trong khung, tạo WO cho BoP chưa có. Chỉ tạo khi giờ UTC hiện tại
  nằm trong khung ca của PS gắn với DWO; giờ timer lại tính theo giờ server (L08).
- Pre-check của team trên các lệnh đổi trạng thái WO đọc bảng role theo transition; role gán cho Frozen → Aborted không
  có trên máy, nên không ai hủy được WO Frozen (L06). Chặn hai WO Active cùng máy cùng bước; chặn xóa WO đã chạy.
- Nợ N-R2-1: service layer chưa kiểm quyền theo role cho từng command.

## Decision

1. **Extension app công bố POM của nó dưới base app.** Command của FB mà extension app import có tên công bố
   `<ExtApp>_<Command>` trên base app; entity và facet nó import đọc được trên base app dưới tên của chúng. Extension app
   không tự nhận lời gọi.
2. **Lớp Project** (`src/Project/Nvm.Project`): `FB_MS_Nv` (PS, DWO, facet `ProcessSchedule` trên process),
   `FB_OP_Nv` (facet `WorkOrderShift` trên WO, lệnh `HandleShiftTimer`, năm pre-check), extension app `NvProductionExt`
   gắn vào `Production`. Tạo PS sinh timer `DAILY_WO_<PS>` (24 giờ) và signal rule gọi
   `NvProductionExt_HandleShiftTimer` qua app Production. Lệch giờ server là configuration key
   `Nv.ServerUtcOffsetMinutes` (thiếu: 0); khung ca so bằng phút UTC như code của team.
3. **Runner suite** (`src/Solution/Nvm.Solution/Suites`): file `*.suite.json` có test definition, step (Command, Read,
   Function, Sql), lặp theo CSV `#TEST_CSV.<cột>#`, tham chiếu `#GET-><step>.Result.<đường dẫn>#`, `ExpectedSuccess`,
   `IfThisSkipNext`, assert theo đường dẫn JSON (`Equals`, `Count`). Tham chiếu không giải được thì bỏ tham số đó. Bước
   deploy `make master-data` chạy suite trong tiến trình host dưới người gọi hệ thống, ghi lịch sử ở
   `suites/.history/MasterDataExecutionHistory.<plant>.txt` (mặc định chỉ khi suite đạt; biến thể "luôn ghi" cho L07).
4. **Quyền gọi command theo role** ở service layer: app bật `RestrictCommands` thì người gọi phải có một role ghi cho
   command, hoặc role quản trị; không thì HTTP 403 mã `6403` (mã tự đặt). Người gọi hệ thống (signal rule, event) và lời
   gọi con không bị kiểm. Manifest của solution (`SolutionManifest`) ghi role cho từng command; host và test dùng chung.
5. **Màn "Lệnh theo ca"** ở khu Xưởng (`/area/line`), lab giao diện bằng Playwright chạy trên stack thật (`make lab-web`),
   dùng Edge đã cài.

## Consequences

**Được**

- Master data NV1 nạp từ DB trống bằng 9 suite; chạy lại không tạo trùng (đọc theo khóa trước khi tạo).
- WO theo ca mô phỏng được nhiều ngày bằng giờ giả; bốn lab L06, L07, L08, L16 có test tái hiện và đối chứng.
- Đóng N-R2-1: lệnh của Production và Foundation có quyền theo role.

**Mất / phải chịu**

- Điều kiện "mọi MTU của WO đã chạy hết hoặc có NC" khi tự hoàn tất chưa dựng: chưa có tin từ xưởng (R5). Hiện WO
  Frozen đủ giờ là Complete.
- Thứ tự ở hệ thật (tự hoàn tất chạy trước đóng băng, đóng băng muộn vài giây) làm WO Complete chậm thêm một lần timer;
  với giờ giả mọi bước cùng một thời điểm nên Complete đúng sau số giờ của BoP.
- Mã `6403`, cấp thiết bị, quy mô dữ liệu (16 khu, mỗi khu một BoP; hệ thật nhiều BoP mỗi khu) là lựa chọn của v2.
- Test đổi giờ chỉ chứng minh server chạy UTC không bị ảnh hưởng; server theo múi giờ có DST chưa mô phỏng (lệch giờ là
  một số cố định).
- Lab giao diện cần cả stack chạy và dữ liệu đã qua ít nhất một lần đóng băng; chưa chạy trong CI.
- Nền chưa có lệnh gỡ role khỏi transition: role gán nhầm (vd khi thử màn Quản trị) chỉ gỡ được bằng SQL, và nó âm thầm
  làm L06 mất tác dụng.

**Việc phát sinh**

- R5: tin từ xưởng gắn sản phẩm vào WO của ca; tạo WO khi tin tới mà chưa có WO.
- R12: chạy lab giao diện trong môi trường thử nghiệm có đủ stack.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| Master data nạp bằng script SQL hoặc seed trong code | Mất đúng thứ cần luyện: suite, CSV, lịch sử, các lỗi L07, L16 |
| `HandleShiftTimer` là composite của app | Hệ thật chạy trong FB (`TimerUtils`); composite mỗi lời gọi một transaction, khác hành vi |
| Kiểm role trong handler thay vì service layer | Hệ thật kiểm quyền gọi command ở nền, pre-check của team kiểm role theo transition: hai lớp khác nhau |
| Tải trình duyệt riêng cho Playwright | 150 MB cho một lab chạy tay; Edge có sẵn đủ dùng |

## Evidence

- `tests/Integration/Nvm.SolutionTests`: 16 test (master data, 3 ngày ca, hai ngày đổi giờ, L06, L07 và đối chứng,
  L08 hai biến thể, L16, pre-check, quyền); `tests/Integration/Nvm.FoundationTests`: extension app công bố POM, quyền theo
  role.
- Deploy thật trên stack local 2026-10-28: `--update-database` (36 entity, 2 bảng facet), `--load-master-data` 9/9
  Executed, chạy lại 9/9 Skipped.
- Lab giao diện trên stack local 2026-10-28: `make lab-web` 3/3, gồm L06 (hủy WO Frozen bị chặn, màn hiện HTTP 400 và
  câu lỗi của pre-check).
- Nguồn: bài `work-order-theo-ca`, `master-data-suite`, `tester-tool-suite`, `role-huy-wo-frozen`,
  `suite-doc-sai-cot-csv` (khóa AMS), `ban-thiet-ke-va-ca` (khóa MOM).
