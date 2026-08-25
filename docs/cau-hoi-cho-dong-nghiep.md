---
title: "Câu hỏi hỏi đồng nghiệp về dự án sắp tới"
created: 2026-08-25
purpose: "Xác định dự án Factory Digitalization sắp tham gia thuộc kịch bản nào, để chỉnh trọng số của scope.md"
tags: [interview-prep, factory-digitalization, discovery]
---

# Câu hỏi hỏi đồng nghiệp về dự án sắp tới

---

## ⭐ NẾU CHỈ HỎI ĐƯỢC MỘT CÂU — hỏi câu này

> *"Anh/chị ơi, em đang tự làm một project nhỏ ở nhà để chuẩn bị trước. Em hình dung hệ thống mình làm đại khái là: **hứng dữ liệu từ máy dưới xưởng, lưu lại để sau này truy ngược được, rồi có màn hình cho người vận hành thao tác**. Em hiểu vậy có đúng không, hay thực tế khác nhiều ạ?"*

### Vì sao là câu này

Nó không phải câu hỏi mở kiểu "hệ thống mình làm gì ạ" — câu đó khiến người ta phải soạn cả một bài giảng, nên họ sẽ trả lời qua loa. Câu trên **đưa sẵn một phỏng đoán cụ thể nhưng khiêm tốn**, và con người thì rất thích sửa một phỏng đoán sai. Bạn sẽ nhận được câu trả lời dài gấp ba lần.

Nó cũng gói được **bốn câu hỏi trong một**, mà không câu nào lộ ra như đang tra hỏi:

| Ẩn trong câu | Bạn sẽ nghe được từ phần họ sửa |
|---|---|
| Hệ thống phục vụ xưởng hay văn phòng? | Họ sẽ nói rõ ai là người dùng |
| Dữ liệu từ máy về hay nhập tay? | Họ sẽ sửa chỗ "hứng dữ liệu từ máy" |
| Có nghiệp vụ truy vết không? | Họ sẽ xác nhận hoặc nói "cái đó bên khác làm" |
| Mình có làm UI không? | Họ sẽ sửa chỗ "màn hình cho người vận hành" |

Vế *"hay thực tế khác nhiều ạ?"* ở cuối là phần quan trọng nhất — nó cho phép họ nói dài mà không thấy phiền.

### Nghe được gì thì ghi lại

Đừng ghi ý, **ghi nguyên văn danh từ họ dùng**:

1. **Tên hệ thống / sản phẩm** — "Opcenter", "SAP", "hệ thống tự viết", "cái tool của khách"…
2. **Tên công đoạn** — mỗi nhà máy có từ vựng riêng. Dùng đúng từ của họ là cách nhanh nhất để được coi là người trong nghề.
3. **Câu bắt đầu bằng "cái khó là…"** — đây là vàng. Ghi nguyên văn.

---

### Hai câu bám theo (chỉ hỏi nếu họ đang hào hứng kể)

**Câu 2 — hỏi ngay sau, nếu không khí đang mở:**

> *"Thế phần nào hay trục trặc nhất ạ?"*

Người ta luôn có sẵn câu trả lời cho câu này và luôn muốn kể. Câu trả lời điển hình: *"dữ liệu máy hay mất/trùng"*, *"truy vết ngược chậm lắm"*, *"mã vật tư mỗi hệ thống một kiểu"*, *"máy dừng là hệ thống cũng treo theo"*.

**Đây chính là danh sách bài tập thật của bạn.** Nghe được câu nào, đẩy milestone tương ứng trong `scope.md` lên làm trước.

**Câu 3 — để dành cho một hôm khác, lúc chào ra về:**

> *"Nếu em muốn chuẩn bị trước ở nhà thì anh/chị khuyên em học gì ạ?"*

Câu kết đẹp, và thường nhận được câu trả lời thẳng thắn nhất. Nhiều khi họ nói ra thứ bạn không nghĩ tới — *"học đọc log cho quen"*, *"SQL giỏi vào là được"*, *"viết mail tiếng Anh cho gọn"*.

---

> [!tip] Đừng hỏi cả ba trong một buổi
> Câu 1 và 2 đi liền được vì chúng nối mạch tự nhiên. Câu 3 để hôm khác. Phần còn lại của tài liệu này là **ngân hàng dự phòng** — chỉ dùng khi có dịp thuận lợi, hoặc khi đồng nghiệp chủ động hỏi ngược "em còn thắc mắc gì không".

---

## Ngân hàng câu hỏi dự phòng

*(Phần bên dưới không cần hỏi hết. Giữ lại để dùng dần khi có cơ hội.)*

> **Cách dùng**: đừng hỏi một lượt 15 câu như phỏng vấn ngược. Chia làm 2–3 lần, xen vào lúc nói chuyện tự nhiên (giờ ăn trưa, sau standup). Mỗi lần 3–5 câu.
>
> **Nguyên tắc**: hỏi bằng ngôn ngữ đời thường. Nếu bạn dùng từ "bounded context" hay "event sourcing" mà chưa làm bao giờ, người ta sẽ nghĩ bạn đang khoe chữ. Hỏi đơn giản, rồi **nghe từ ngữ họ dùng** — chính từ ngữ đó mới là thứ đáng ghi lại.

---

## Cách mở đầu (để không bị hiểu là dò xét)

> *"Em đang tự làm một project nhỏ để chuẩn bị trước cho dự án. Anh/chị cho em hỏi vài câu về hệ thống mình đang làm được không ạ, để em học đúng thứ cần dùng, khỏi học lệch."*

Câu này rất hiệu quả vì nó nói rõ động cơ: bạn muốn chuẩn bị, không phải tò mò.

---

## Nhóm A — Hệ thống mình đang làm là gì?

### A1. Phần mềm mình làm phục vụ ai?

> *"Phần mềm mình đang làm chủ yếu phục vụ ai ạ — người đứng máy dưới xưởng, hay người lập kế hoạch trên văn phòng?"*

**Vì sao hỏi**: phân biệt MES (dưới xưởng, tính bằng phút/giây) với ERP (văn phòng, tính bằng ngày/tuần). Hai thế giới khác hẳn nhau về yêu cầu.

**Đổi gì trong scope**: nếu là "dưới xưởng" → giữ nguyên toàn bộ phần ingestion, realtime, takt time. Nếu là "văn phòng" → giảm phần thiết bị, tăng phần báo cáo và tích hợp ERP.

---

### A2. Mình tự viết hay dùng phần mềm mua sẵn?

> *"Mình tự viết hệ thống từ đầu, hay là mình cấu hình / mở rộng một phần mềm có sẵn của hãng ạ?"*

Nếu họ nói "phần mềm có sẵn", hỏi tiếp:

> *"Của hãng nào ạ? Em có nghe tới Siemens Opcenter, không biết mình có dùng không."*

**Vì sao hỏi**: đây là câu quan trọng nhất. Tự viết = bạn thiết kế; mở rộng sản phẩm = bạn phải theo khuôn của sản phẩm đó.

**Đổi gì trong scope**:
- Nếu là **Opcenter** → phần "lớp ánh xạ OEF" trong `scope.md` thành trọng tâm, và bạn cần học thêm Visual Studio add-in, cách đóng gói package.
- Nếu **tự viết .NET** → phần .NET core là trọng tâm, Opcenter chỉ còn là từ vựng chung.

---

### A3. Ngôn ngữ và cơ sở dữ liệu chính?

> *"Team mình code chính bằng gì ạ — C# hay Java? Với database là SQL Server hay cái khác?"*

**Vì sao hỏi**: JD ghi "C#/Java" nên có thể là cả hai. Opcenter chạy trên SQL Server, còn nhiều dự án khác dùng PostgreSQL hoặc Oracle.

**Đổi gì trong scope**: quyết định `scope.md` dùng SQL Server làm store chính hay chuyển hẳn sang PostgreSQL. Hiện đang thiết kế **cả hai** (SQL Server cho nghiệp vụ, PostgreSQL cho dữ liệu máy) — an toàn nhất nhưng nếu biết chắc thì bỏ bớt được một cái.

---

## Nhóm B — Việc của em cụ thể là gì?

### B1. Người mới thường bắt đầu từ việc gì?

> *"Người mới vào team thường được giao việc gì đầu tiên ạ? Em muốn chuẩn bị trước đúng chỗ đó."*

**Vì sao hỏi**: câu này vừa lấy được thông tin, vừa thể hiện thái độ chủ động. Rất khó bị từ chối.

**Đổi gì trong scope**: quyết định milestone nào bạn làm trước. Nếu người mới thường được giao "viết API báo cáo" thì bạn nên đẩy phần read model/query lên sớm hơn.

---

### B2. Có phải làm cả giao diện không?

> *"Em có phải làm cả phần giao diện không, hay chỉ làm phía sau (backend) thôi ạ?"*

Nếu có giao diện, hỏi tiếp:

> *"Giao diện mình làm bằng gì ạ? Em có học qua Mendix, không biết có dùng được không."*

**Vì sao hỏi**: bạn đã chốt làm toàn bộ UI bằng Mendix trong project học. Cần biết có khớp không.

**Đổi gì trong scope**: nếu thực tế dùng React/Angular thì phần Mendix vẫn đáng học (vì Siemens dùng), nhưng nên giảm từ 5 màn hình xuống 2–3.

---

### B3. Phần khó nhất của hệ thống này là gì?

> *"Theo anh/chị, phần khó nhất hoặc hay gặp lỗi nhất của hệ thống này là gì ạ?"*

**Vì sao hỏi**: đây là câu cho ra thông tin giá trị nhất và người ta thích trả lời. Câu trả lời thường là: "dữ liệu từ máy hay mất/trùng", "truy vết ngược chậm", "master data lệch nhau", "máy dừng thì hệ thống cũng dừng theo".

**Đổi gì trong scope**: chỗ nào họ nói khó → bạn đẩy milestone đó lên sớm và làm kỹ hơn. Đây là cách tốt nhất để "học sát production".

---

## Nhóm C — Dữ liệu và kết nối

### C1. Dữ liệu từ máy móc về bằng cách nào?

> *"Dữ liệu từ máy dưới xưởng gửi về hệ thống bằng cách nào ạ? Máy tự gửi, hay mình đi đọc, hay là file?"*

**Vì sao hỏi**: ba cách này ra ba kiến trúc khác nhau. "Máy tự gửi" = message broker. "Mình đi đọc" = polling OPC UA. "File" = file watcher, thường là hệ thống cũ.

**Đổi gì trong scope**: hiện `scope.md` làm cả ba (MQTT, OPC UA, CSV drop). Biết rồi thì tập trung vào cái thật.

---

### C2. Một ngày về bao nhiêu dữ liệu?

> *"Một ngày hệ thống nhận khoảng bao nhiêu dữ liệu ạ? Vài nghìn bản ghi hay là nhiều hơn nhiều?"*

**Vì sao hỏi**: quyết định có cần tách kho dữ liệu máy ra riêng không. Vài nghìn/ngày thì một database là đủ; vài triệu/ngày thì bắt buộc phải tách.

**Đổi gì trong scope**: giữ hay bỏ TimescaleDB.

---

### C3. Có nối với hệ thống nào khác không?

> *"Hệ thống mình có nối với ERP hay hệ thống nào khác của khách không ạ? Nối kiểu gì — gọi API hay đẩy file?"*

**Vì sao hỏi**: ranh giới ERP ↔ MES là chỗ tranh cãi nhiều nhất trong mọi dự án nhà máy. Biết trước thì đỡ bỡ ngỡ.

**Đổi gì trong scope**: giữ hay bỏ phần B2MML/SFTP.

---

## Nhóm D — Cách team làm việc

### D1. Có yêu cầu gì đặc biệt về lưu vết không?

> *"Dữ liệu trong hệ thống có phải giữ lâu không ạ, hay là có kiểm toán gì không? Em nghe nói ngành ô tô hay bị audit."*

**Vì sao hỏi**: nếu có audit (IATF 16949 chẳng hạn) thì "không được xoá, không được sửa" là ràng buộc cứng, và event sourcing có lý do thật. Nếu không thì đó là over-engineering.

**Đổi gì trong scope**: mức đầu tư vào event sourcing và audit trail.

---

### D2. Test và deploy thế nào?

> *"Bên mình có viết test tự động không ạ? Deploy lên môi trường khách thì làm sao?"*

**Vì sao hỏi**: biết mức trưởng thành của quy trình. Cũng là gợi ý bạn nên luyện gì (Testcontainers? CI pipeline? deploy thủ công?).

**Đổi gì trong scope**: mức đầu tư vào §10 (test) và §12 (CI/CD).

---

### D3. Nếu muốn tự học trước thì nên học gì?

> *"Nếu em muốn tự học trước ở nhà thì anh/chị khuyên em học gì ạ?"*

**Vì sao hỏi**: câu kết đẹp, và thường nhận được câu trả lời thẳng thắn nhất. Nhiều khi họ nói ra thứ bạn không nghĩ tới (ví dụ: "học đọc log", "học SQL cho giỏi vào", "học tiếng Anh viết mail").

**Đổi gì trong scope**: có thể thêm hẳn một milestone mới.

---

## Bảng ghi câu trả lời

Điền vào đây rồi báo lại, tôi sẽ chỉnh `scope.md` theo:

| Mã | Câu hỏi rút gọn | Câu trả lời | Ngày hỏi |
|---|---|---|---|
| A1 | Phục vụ xưởng hay văn phòng? | | |
| A2 | Tự viết hay mở rộng sản phẩm? Hãng nào? | | |
| A3 | Ngôn ngữ + database chính? | | |
| B1 | Người mới làm việc gì đầu tiên? | | |
| B2 | Có làm UI không? Bằng gì? | | |
| B3 | Phần khó nhất / hay lỗi nhất? | | |
| C1 | Dữ liệu máy về bằng cách nào? | | |
| C2 | Lượng dữ liệu mỗi ngày? | | |
| C3 | Nối ERP / hệ thống khác? Kiểu gì? | | |
| D1 | Yêu cầu lưu vết / audit? | | |
| D2 | Test tự động? Deploy thế nào? | | |
| D3 | Nên tự học gì trước? | | |

---

## Ba từ khoá cần nghe ngóng

Không cần hỏi thẳng, chỉ cần để ý xem đồng nghiệp có nhắc tới không. Nghe thấy từ nào thì ghi lại:

1. **Tên sản phẩm/hệ thống** — "Opcenter", "SAP", "Camstar", "Ignition", "Wonderware", "hệ thống tự viết"…
2. **Tên bước công đoạn** — họ gọi công đoạn là gì? Mỗi nhà máy có từ vựng riêng, và dùng đúng từ của họ là cách nhanh nhất để được coi là người trong nghề.
3. **Tên thứ hay hỏng** — "mất dữ liệu", "trùng bản ghi", "chậm", "máy không gửi", "sai mã vật tư". Đây là danh sách bài tập thật của bạn.

---

## Điều KHÔNG nên hỏi

- Đừng hỏi lương, đừng hỏi ai sắp nghỉ, đừng hỏi đánh giá về sếp.
- Đừng hỏi chi tiết bảo mật kiểu "mật khẩu database là gì", "IP server bao nhiêu" — kể cả khi bạn chỉ tò mò kỹ thuật.
- Đừng hỏi thông tin khách hàng cụ thể (tên nhà máy, sản lượng thật) rồi mang ra ngoài. Trong project học của bạn, dùng số liệu hư cấu trong `scope.md` là đủ.

---

*Liên kết: [scope.md](scope.md) · [[Chuẩn bị Audit đơn vị Factory Digitalization - 08-07-2026]]*
