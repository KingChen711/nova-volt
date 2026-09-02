# ADR-033 — MinIO Object Lock cho bản gốc đường cong formation

- **Trạng thái**: Accepted
- **Ngày**: 2026-08-31
- **Milestone**: M3 · C12

## Bối cảnh

Hypertable giữ telemetry đã được decode, chuẩn hoá identity và phân loại `clock_quality`. Nó phù hợp
cho truy vấn kỹ thuật quy trình, nhưng không còn là đúng file mà thiết bị đã xuất. Khi điều tra một
sự cố chất lượng, auditor phải có thể lấy lại **đúng byte gốc**, chứng minh file chưa đổi, và chạy lại
pipeline bằng code hiện tại.

`sha256` chỉ chứng minh nội dung nếu bản băm tham chiếu và object được giữ bất biến. Một bucket S3 có
versioning nhưng không có retention vẫn cho phép xoá version. Ngược lại, Object Lock không tự bảo đảm
"một key chỉ có một bản": lock áp dụng trên **từng version**, nên một `PUT` cùng key vẫn có thể sinh
thêm version WORM. Một lệnh xoá không chỉ rõ version còn có thể tạo delete marker che current object,
dù version bị khoá vẫn tồn tại.

Đường ghi còn có hai tài nguyên không nằm trong cùng transaction: MinIO và PostgreSQL. Thứ tự sai có
thể để DB trỏ tới object chưa từng được tạo — một hồ sơ nhìn hợp lệ nhưng không phục hồi được.

## Quyết định

1. File CSV gốc nằm trong bucket riêng `raw-curve`, có versioning và default retention
   **`COMPLIANCE 15 years`**. Repo provision bucket bằng `mc mb --with-lock` rồi kiểm lại retention.
2. Key có dạng `site/yyyy/MM/dd/<sha256>.csv`. Đây là content-addressed identity; `site_id` đứng đầu
   key để đường lưu trữ không trộn site.
3. Client tính SHA-256 trên exact stream **trước upload**, khôi phục đúng vị trí stream rồi gửi đồng
   thời checksum S3 và metadata `site-id`, `archive-id`, `sha256`.
4. Tạo object bằng conditional request `If-None-Match: *`. `HEAD` rồi `PUT` không đủ: hai uploader
   đồng thời đều có thể thấy key chưa tồn tại rồi tạo hai version không xoá được.
5. PostgreSQL lưu `object_key` **và exact `object_version_id`**. Mọi lần đọc/kiểm đều chỉ rõ version,
   không phụ thuộc current version hay delete marker.
6. Ghi object trước, append row `ts.raw_curve_archive` sau. `archive_id` là UUIDv5 của
   site/equipment/unit/interval/digest; retry sau khi process chết giữa hai bước sẽ gặp object cũ qua
   conditional write rồi append lại row còn thiếu. Trạng thái tạm có thể là object mồ côi, nhưng không
   có trạng thái DB đã commit mà object chưa tồn tại.
7. Metadata DB là append-only: trigger từ chối `UPDATE` và `DELETE` bằng SQLSTATE `P1201`. Sửa sai
   nghĩa là lưu một archive mới với digest mới, không sửa hồ sơ cũ.
8. Tất cả query metadata nhận `site_id` từ caller và ép filter phía server. `site_id` của lúc ghi được
   suy ra từ `EquipmentPath`, không nhận một tham số rời có thể mâu thuẫn.
9. **Mỗi archive row phải nói ai ghi, vì sao, và sửa bản nào** — `actor`, `reason`,
   `supersedes_archive_id` (migration `009`). Điểm 7 đã cho đúng *hình dạng* của K5 nhưng chưa cho
   *nội dung*: hai row cùng channel cùng khoảng thời gian mà không có lời giải thích nào thì auditor
   thấy bằng chứng đã đổi và không biết ai đổi hay vì sao. `supersedes_archive_id` là `UNIQUE`
   (partial, khi khác `NULL`): hai row cùng nhận sửa một bản gốc là hai câu trả lời và không có cách
   chọn. Bản bị sửa **không** bị xoá — cả row lẫn object đều ở lại.
10. **Có caller thật và chỉ một snapshot**, không chỉ có primitive. Adapter atomically rename file
    khỏi public inbox vào `.processing`, đọc bytes **đúng một lần**, rồi dùng cùng buffer đó cho parse,
    WORM archive và bản dưới `processed`/`rejected`. Nếu exporter tạo lại cùng public path trong lúc
    ingest, file mới là lượt kế tiếp; nó không thể thay "bản gốc" của telemetry đang ghi. MinIO không
    tới được thì exact snapshot được trả về inbox để poll sau retry; claim còn lại sau process crash
    cũng được phục hồi lúc watcher khởi động. Một export phải có ít nhất một measurement hợp lệ và quy
    về đúng một máy; file nhiều máy, có identity không đọc được, hoặc chỉ có header đều bị từ chối
    **toàn file trước khi ingest**. Dòng lỗi value của cùng máy vẫn được tách thành artefact `rejected`,
    còn exact byte của cả export vẫn được archive trước khi snapshot sang `processed`.

## Client và license

| Thành phần | Version | License | Vai trò |
|---|---:|---|---|
| `AWSSDK.S3` | `4.0.102.4` | Apache-2.0 | Client runtime; chọn vì expose `PutObjectRequest.IfNoneMatch` |
| `Testcontainers.Minio` | `4.14.0` | MIT | Chỉ dùng trong integration test, chạy image MinIO đã pin |
| `minio/minio` | `RELEASE.2025-09-07T16-13-09Z` | AGPL-3.0 | Server nguyên bản trong container; không sửa hay phân phối binary dẫn xuất |

Kiểm ngày 2026-08-31 từ metadata NuGet và license upstream. Không chọn MinIO .NET SDK dù license
Apache-2.0 vì API upload của nó không cung cấp conditional create cần cho invariant một version.

## Hệ quả

### Được

- Một phép tải exact version + băm lại trả lời được "đây có đúng byte đã lưu không".
- Retry idempotent không tạo version WORM thứ hai của cùng raw curve.
- Delete marker hoặc object mới cùng key không đổi evidence mà DB đã tham chiếu.
- Database không chứa blob lớn; index vẫn query được theo site/equipment/khoảng thời gian.

### Mất

- `COMPLIANCE` không có nút bypass cho vận hành; file sai vẫn chiếm dung lượng đủ 15 năm. Đây là
  chủ ý pháp lý, không phải sự cố cần workaround.
- Không có distributed transaction. Có thể còn object không có row nếu process chết sau `PUT`; retry
  cùng descriptor tự hoà giải. Cần một orphan scanner khi đường upload được đưa vào vận hành ở M7.
- Content-addressed key làm hai descriptor có exact bytes giống nhau dùng chung object version; mỗi
  descriptor vẫn có metadata row riêng. Object là bằng chứng nội dung, row là ngữ cảnh nhà máy.
- `EnsureRetentionAsync` thêm một S3 request cho mỗi lần archive. Khi có tải thật có thể cache cấu hình
  ngắn hạn, nhưng chỉ sau khi đo và vẫn phải fail closed nếu cấu hình hết hạn hoặc sai.
- Archive **chặn** đường file-drop: MinIO chết thì file dừng ở inbox thay vì chảy tiếp. Đổi lại,
  không có file nào vào `processed` mà bản gốc không được giữ. Đây là đánh đổi có chủ ý cho hồ sơ
  pháp lý; đường MQTT không bị ảnh hưởng.
- `actor` của adapter là `ingestion:file-drop`, tức **danh tính của luồng**, chưa phải của con người.
  Con người nào thả file lên share là câu hỏi của identity ở M13; ADR này chỉ bảo đảm cột không bao
  giờ trống và không bao giờ là một service account vô danh.

## Những phương án không chọn

| Phương án | Không chọn vì |
|---|---|
| Chỉ lưu telemetry đã decode trong TimescaleDB | Không còn exact file thiết bị; không thể tách bug pipeline khỏi dữ liệu nguồn |
| Lưu blob trong PostgreSQL | Ghép retention pháp lý với backup/restore của read store và làm database phình không cần thiết |
| Bucket có versioning nhưng không Object Lock | Admin vẫn xoá được exact version; versioning một mình không phải WORM |
| `HEAD` rồi `PUT` | Có race; hai uploader có thể tạo hai version WORM cùng key |
| Chỉ lưu `object_key` | Current version có thể đổi hoặc bị delete marker che; không biết version nào đã được băm |
| Ghi DB trước rồi upload | Process chết để lại metadata hợp lệ bề ngoài nhưng object không tồn tại |
| `GOVERNANCE` retention | Principal có quyền bypass có thể xoá evidence; không đạt cam kết 15 năm của raw curve |

## Evidence tái lập

Integration test chạy PostgreSQL/TimescaleDB và MinIO thật, không mock S3:

```powershell
dotnet test --project tests/Integration/Nvm.IntegrationTests/Nvm.IntegrationTests.csproj `
  --filter-class Nvm.IntegrationTests.RawCurveArchiveTests --output Normal
```

Kết quả ngày 2026-08-31 với image `minio/minio:RELEASE.2025-09-07T16-13-09Z`:

- **2/2 test xanh** trong **19,597 s**;
- upload cùng descriptor và exact bytes hai lần → **1 object version, 1 metadata row**;
- tải exact version → byte-for-byte bằng input và SHA-256 khớp;
- lật 1 byte trong bản tải → phép kiểm trả `false`;
- cùng `archive_id` query bằng `NV1` thấy **1**, bằng `DE1` thấy **0**;
- xoá exact version bị MinIO từ chối bằng **HTTP 400**, code **`InvalidRequest`**, message chứa
  **`Object is WORM protected`**;
- `UPDATE` và `DELETE` metadata đều bị PostgreSQL từ chối bằng **`P1201`**;
- chạy Down migration 006 xoá raw-curve index nhưng continuous aggregate C07 vẫn còn.

Regression `ReplacingThePublicPathAfterClaim_DoesNotChangeTelemetryArchiveOrProcessedBytes` chặn
ingestor sau parse, ghi file B vào lại đúng public path rồi mới cho transaction tiếp tục. Oracle xác
nhận telemetry, archive và `processed` đều thuộc snapshot A byte-for-byte; B vẫn nằm nguyên trong
inbox cho lần poll kế tiếp. Test log riêng còn ghim identity không đọc được vào EventId **2511**, không
được phát nhầm EventId 2508 của file nhiều máy.

Lệnh compose kiểm provisioning của bucket:

```powershell
docker compose run --rm minio-init
```

Output ngày 2026-08-31 có đúng `Object locking 'COMPLIANCE' is configured for 15YEARS.`; đây là
kiểm cấu hình thật, không suy ra từ YAML.
