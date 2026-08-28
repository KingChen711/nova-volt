# Seed của bài test/load — topology 1.000 kênh

Một catalog **riêng**, không phải revision mới của [`deploy/seed/`](../seed/README.md).

## Vì sao tách ra

`scope.md` §9/M2 chốt hai điều cùng lúc, và chúng kéo ngược nhau:

- **Seed demo giữ 8 kênh.** Nó là file người ta mở ra để hiểu cây nhà máy. Một file 1.000 dòng
  thì không ai đọc, và quyết định G1 nói thẳng là *"không nhất thiết biến seed demo mặc định
  thành 1.000 dòng viết tay"*.
- **Bài test/load chạy 1.000 kênh**, vì cardinality của alias table và session là phần **thật**
  của ingestion chứ không phải của simulator.

Thêm `factory-model.r4.json` vào `deploy/seed/` không giải được: `LatestRevision` sẽ thành r4 và
**mọi** process mặc định nhảy sang 1.000 kênh, trừ khi pin revision ở từng chỗ — một cách sửa mà
lần đầu ai đó quên pin là lần đó demo im lặng đổi hình dạng.

## Cardinality này đo cái gì

Sparkplug cho một edge node **một** `seq` và **một** `bdSeq`, nhưng mỗi device dưới nó có **bảng
alias riêng**, khai đúng một lần trong `DBIRTH` của nó và không bao giờ nói lại. 1.000 device là
1.000 bảng sống cùng lúc dưới một phiên.

Nếu chúng không được giữ tách bạch thì hỏng theo kiểu **im lặng và không sửa được trong phiên đó**:
alias 7 là một metric ở kênh này và một metric khác ở kênh kia, nên một bảng dùng chung **không
báo lỗi** — nó gán nhiệt độ vào chỗ điện áp rồi ghi xuống. Một phép đo không ai biết là sai thì tệ
hơn một phép đo bị từ chối.

## Cách dùng

Sinh lại file (không sửa tay):

```
python deploy/seed-load/make-load-topology.py
```

Chạy D2 trên topology này — **gateway và harness phải cùng giá trị**, nếu không gateway từ chối
mọi topic harness phát ra, đúng theo K3:

```
NVM_SEED_DIR=seed-load make load
```

Mệnh đề cardinality được ép trong `ThousandChannelTopologyTests` (`tests/Unit`), chạy trong
`make ci` và không cần Docker: nó nạp đúng file này, khai 1.000 `DBIRTH` dưới một `NBIRTH`, rồi
đọc `DDATA` chỉ-có-alias trên cả 1.000 kênh.

> [!warning] Chỗ này từng hỏng, và hỏng im lặng
> Bản đầu của harness dựng danh sách kênh bằng cách **đoán tên**: hỏi `FORM-01-CH-0001` tới
> `FORM-01-CH-2000` rồi giữ những mã có thật. Đúng với seed demo (một cycler), nhưng với thư mục
> này (mười cycler) nó chỉ thấy **100 / 1.000** kênh — và vẫn chạy, vẫn in ra một con số, chỉ là
> con số của một nhà máy khác.
>
> Harness nay đọc `model.Paths` và lọc theo prefix, đúng cách simulator vẫn làm. Kiểm bằng chính
> dòng banner nó in: `across 1000 channels` với `seed-load`, `across 8 channels` với seed demo.
> Bài học chung với K3: **thành phần nào tự có ý kiến về tên thiết bị là thành phần sẽ sai** khi
> nhà máy đổi hình dạng.

## Quy tắc

1. **Không sửa tay `factory-model.r1.json`.** Nó là output. Sửa `make-load-topology.py` rồi chạy lại.
2. **Cùng `LINE_PATH` với seed demo.** Topic `NOVAVOLT/NV1/FORMATION/F1/...` là thứ gateway resolve;
   đặt tên khác là đang đo một nhà máy khác.
3. Đây **không phải** hồ sơ traceability, nên quy tắc *"đổi cây = thêm file mới"* của `deploy/seed/`
   không áp dụng ở đây. Không có run nào của production đọc thư mục này.
