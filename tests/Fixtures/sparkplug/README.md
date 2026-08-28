# Payload Sparkplug B thật

Hai file `.bin` ở đây là **byte thật trên dây** của một kênh sạc formation, cặp đôi với nhau:
một `DBIRTH` và một `DDATA` ngay sau đó.

Chúng tồn tại để trả lời đúng một câu hỏi: *code sinh từ `sparkplug_b.proto` có đọc được payload
mà nó **không tự tạo ra** hay không.*

## Luật

1. **Không được sinh bằng `Nvm.Sparkplug`.** Đây là luật quan trọng nhất của thư mục này, và nó
   cùng họ với luật số 1 của [`tests/Contract/golden/`](../../Contract/golden/README.md): một
   phép kiểm encode-rồi-decode bằng chính một bộ code sẽ **xanh cả khi cả hai chiều cùng sai**.
   Schema chính là loại thứ sai được cả hai chiều một lúc — sửa một field number thì encoder và
   decoder vẫn đồng ý với nhau, và chỉ bất đồng với thiết bị thật.
2. **Người sinh ra chúng là [`pysparkplug`](https://pypi.org/project/pysparkplug/) 0.6.1**, thư
   viện Python mang **bản schema của riêng nó** (`pysparkplug/_protobuf/sparkplug_b_pb2.py`),
   không đọc file `.proto` trong repo này. Đó là chỗ tính độc lập nằm.
3. **Không regenerate cho vui.** File sinh lại mỗi lần build là file không chứng minh gì. Chỉ chạy
   lại khi cần thêm một ca mới, và khi đó ghi lại số byte + SHA-256 vào bảng dưới.
4. **`.bin` là nhị phân**, khai ở `.gitattributes`. Một lần Git chuẩn hoá CRLF trên file này là
   payload hỏng, và triệu chứng sẽ là một test decode đỏ chứ không phải một cảnh báo nào.

## Danh sách

| File | Byte | SHA-256 | Nội dung |
|---|---|---|---|
| `dbirth-form-01-ch-0142.bin` | 213 | `368b0bd2…d6b0c1` | `DBIRTH`, `seq=1`, **5 metric** khai đủ name + alias + datatype |
| `ddata-form-01-ch-0142.bin` | 41 | `0886d858…b722b2` | `DDATA`, `seq=2`, **2 metric** chỉ có alias — report-by-exception |

Thiết bị giả định, theo `scope.md` §7.1:

```
spBv1.0/NOVAVOLT-NV1-FORMATION/DBIRTH/EDGE-F1/FORM-01-CH-0142
spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142
```

Topic **không** nằm trong file — Sparkplug để định danh thiết bị ở topic MQTT, không ở payload.
Đó là lý do C03 phải ánh xạ topic sang `equipment_path` riêng, và là lý do một payload nằm một
mình thì không biết nó của máy nào.

## Nội dung, đọc bằng `protoc`

Tự kiểm lại được, không cần chạy test — dùng chính file `.proto` đã vendored:

```bash
"$HOME/.nuget/packages/grpc.tools/2.83.0/tools/windows_x64/protoc.exe" \
  --proto_path=src/Platform/Nvm.Sparkplug/proto \
  --decode=org.eclipse.tahu.protobuf.Payload \
  src/Platform/Nvm.Sparkplug/proto/sparkplug_b.proto \
  < tests/Fixtures/sparkplug/ddata-form-01-ch-0142.bin
```

`DBIRTH` — mỗi metric khai **tên, alias, kiểu**:

```
timestamp: 1787901330500
metrics { name: "Formation/Voltage"     alias: 1  timestamp: 1787901330500  datatype: 9   float_value: 3.6875 }
metrics { name: "Formation/Current"     alias: 2  timestamp: 1787901330500  datatype: 9   float_value: 1.25 }
metrics { name: "Formation/Temperature" alias: 3  timestamp: 1787901330500  datatype: 9   float_value: 31.5 }
metrics { name: "Formation/StepIndex"   alias: 4  timestamp: 1787901330500  datatype: 3   int_value: 2 }
metrics { name: "Formation/CellSerial"  alias: 5  timestamp: 1787901330500  datatype: 12  string_value: "NV1CL16238A00123" }
seq: 1
```

`DDATA` — **không có tên, không có datatype**, và chỉ có 2 trong 5 metric:

```
timestamp: 1787901332500
metrics { alias: 1  timestamp: 1787901332500  float_value: 3.71875 }
metrics { alias: 3  timestamp: 1787901332500  float_value: 31.75 }
seq: 2
```

Đó là **report-by-exception**: dòng và step index không đổi nên không lên dây. Hệ quả nghiệp vụ,
không phải chi tiết định dạng — *"không nhận được gì từ kênh này"* có thể là **cell vẫn đang sạc
bình thường**, và cũng có thể là **mất kết nối**. Phân biệt hai thứ đó là việc của `NDEATH`
(C11), không phải của một cái timeout.

## Vì sao giá trị lại là 3,6875 V chứ không phải 3,65 V

Cả năm giá trị đều **biểu diễn chính xác được bằng binary32**, nên test C# assert bằng phép so
sánh **bằng đúng**, không dùng sai số. Một phép so sánh có sai số cũng sẽ xanh khi decoder đọc
nhầm bốn byte mà rơi vào chỗ gần đúng — và đó chính là loại lỗi những file này tồn tại để bắt.

3,6875 V và 31,5 °C vẫn là số đọc được thật của một cell lithium đang trong formation; chọn giá
trị chính xác không có nghĩa là chọn giá trị vô lý.

## Sinh lại

```bash
python -m venv .venv
.venv/Scripts/pip install pysparkplug==0.6.1
.venv/Scripts/python tests/Fixtures/sparkplug/make-fixtures.py
```

`make-fixtures.py` **không** nằm trong `make build`, `make test` hay CI. Repo này không có
toolchain Python và không cần có: script ở đây là **xuất xứ**, để người đọc dựng lại được byte
thay vì phải tin vào bảng phía trên.
