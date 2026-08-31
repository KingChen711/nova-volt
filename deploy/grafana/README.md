# Grafana — dashboard formation của M3

## Vì sao có dashboard này

`formation-overview.json` là dashboard đầu tiên đọc historian của NovaVolt. Mục tiêu không phải
trang trí: một kỹ sư quy trình phải nhìn được đoạn **CC → CV**, nhiệt độ của cùng channel, mật độ
sample và những row có đồng hồ không đáng tin trước khi dùng số liệu để kết luận.

Dashboard là file provisioned, `editable: false`. Nút **Make editable** chỉ tạo một bản copy trong
SQLite của Grafana; file trong Git vẫn là nguồn sự thật và lần dựng môi trường mới không phụ thuộc
vào thao tác UI.

## Hợp đồng đọc dữ liệu

- Mọi query đọc schema **`ts_scoped`**, không đọc `ts`. Role `nvm_grafana` **không có quyền nào**
  trên `ts`, nên không có câu SQL nào nó viết được mà chạm tới site khác.
- `site` là biến bắt buộc, mặc định `NV1`; danh sách lấy từ `ts_scoped.readable_site` — tức là đúng
  những site mà **connection này** được cấp, không phải mọi site có trong dữ liệu.
- `equipment` là một formation channel thuộc site đang chọn.
- Khoảng rộng hơn 6 giờ đọc `ts_scoped.process_signal_1m`; khoảng 6 giờ trở xuống đọc raw
  `ts_scoped.telemetry_measurement`. Cùng một panel cho phép overview rẻ và zoom điều tra chi tiết.
- Panel `Telemetry samples per device minute` dùng `sum(sample_count)` trên rollup. Đây là **sample
  volume theo `device_timestamp`**, không phải ingestion throughput theo `recorded_at`. Plan C13 gọi
  nó là throughput; dashboard sửa tên vì hai đại lượng không thay thế nhau.
- `clock_quality <> 'Good'` được đếm theo giờ cho toàn site. Không có row xấu vẫn trả series giá trị
  0, thay vì chữ `No data` dễ bị hiểu là query hỏng.

Datasource đăng nhập bằng `nvm_grafana`: role có `SELECT` trên schema `ts_scoped` (view), **không**
có quyền nào trên `ts`, không có quyền ghi, `default_transaction_read_only=on`, statement timeout 30
giây và tối đa 10 connection. Password chỉ nằm trong `.env`; provisioning file dùng environment
substitution.

## Ranh giới bảo mật — cái đã đóng và cái chưa

**Đã đóng (K3).** Trước đây biến `site` chỉ là filter hiển thị: role đọc được cả schema `ts`, nên mở
panel editor rồi gõ một site khác là đọc được nhà máy khác. Audit M3 gọi đúng tên nó là rò rỉ
cross-site. Migration `008` đóng bằng cách **ép ở server**:

- `ts.site_read_grant` ghi role nào đọc được site nào. Role đọc **không thấy** bảng này.
- Schema `ts_scoped` chỉ chứa view lọc theo `current_user`; view chạy bằng quyền của **chủ view** nên
  người đọc không cần — và không có — quyền nào trên `ts`.
- `make up-obs` ghi danh sách site từ biến `NVM_GRAFANA_SITES`. Đây là quyết định của **deployment**,
  không phải của dashboard, và cũng **không** phải cách vá bị plan C13 cấm (khoá cứng một site vào
  file datasource).

**Chưa đóng (M13).** Cấp này authorize một **connection**, không phải một **con người**: mọi người
dùng chung một instance Grafana vẫn dùng chung một grant. Ai đăng nhập là câu hỏi của OIDC qua
Keycloak ở M13. Cho tới lúc đó, đừng quảng bá dashboard này như cổng dữ liệu đa tenant — nhưng nó
**không** còn là đường đọc sang site mà deployment chưa từng cấp.

## Image và license

Pin `grafana/grafana:13.2.0`, bản OSS phát hành tháng 08/2026. Grafana mặc định dùng
**AGPL-3.0-only**; repo chạy nguyên image chính thức, không sửa hay nhúng binary Grafana vào sản phẩm.
Kiểm ngày 2026-08-31 từ [licensing upstream](https://github.com/grafana/grafana/blob/main/LICENSING.md)
và [tài liệu image OSS](https://grafana.com/docs/grafana/latest/setup-grafana/configure-docker/).

## Kiểm chứng

```bash
make up-obs
make net-check
make grafana-net-check
```

Kỳ vọng: stack healthy; datasource báo `Database Connection OK`; K11 nền vẫn **9/9**; Grafana
**3/3** — chỉ ở `it-net`, tới được `timescale:5432`, không tới được `emqx:1883`. Sau đó mở:

```text
http://localhost:3000/d/formation-overview/formation-overview
```

Chọn một cửa sổ 12 giờ của `FORM-01-CH-0001`: legend phải có Voltage, Current, Temperature và đồ thị
phải cho thấy Voltage tăng trong CC, giữ gần 4,2 V trong CV trong lúc Current giảm.

## Evidence 2026-08-31

- `make up-obs`: tất cả service healthy; rerun từ image cache **13 s**.
- datasource API: `Database Connection OK`, user `nvm_grafana`, dashboard **4 panel**.
- role probe: `SELECT = true`, `INSERT = false`, `default_transaction_read_only = on`; một
  `DELETE ... WHERE false` bị từ chối trước khi chạm dữ liệu.
- `make net-check`: **9/9**; `make grafana-net-check`: **3/3**.
- Browser thật, cửa sổ `[2026-07-20T00:00Z, 2026-07-20T12:00Z]`: Voltage **3,00 → 4,20 V**;
  Current **500 mA** ở CC rồi giảm trong CV; Temperature **24,9 → 31,9 °C**; non-good clock **0**;
  **0 console error, 0 warning**.
- Zoom còn 4 giờ chạy nhánh raw và vẫn trả cả Voltage, Current, Temperature; cửa sổ 12 giờ chạy
  nhánh rollup. Đây là kiểm hai nhánh của cùng dashboard, không chỉ kiểm file JSON parse được.
