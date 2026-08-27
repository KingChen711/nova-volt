# Audit M0/M1 — backlog sửa chữa

Kết quả audit độc lập ngày **2026-08-27**, chạy trên HEAD `0f238bc` (M1 đã commit tới C18, C19 chưa).

File này tồn tại vì một lý do rất cụ thể: backlog ban đầu chỉ nằm trong một prompt hội thoại. Hết
context là mất sạch, và không agent nào sau đó biết còn nợ gì. **Mỗi repair unit là một commit riêng.**

> [!important] File này có tuổi thọ, và phải bị XOÁ
> Đây là **tài liệu tạm**, không phải tài liệu sống như [`benchmarks.md`](benchmarks.md) hay
> [`glossary.md`](glossary.md). Khi R1–R5 đều `xong`, việc cuối cùng là **xoá file này** trong một
> commit riêng: `chore(docs): remove m0/m1 audit backlog`.
>
> Lý do không giữ lại làm kỷ niệm: thứ đáng giữ đã nằm đúng chỗ của nó rồi — số đo ở
> `plans/M0-bootstrap.md` §C08.4, quyết định ở `adr/`, giới hạn còn lại ở XML doc của chính đoạn code
> có giới hạn đó. Giữ thêm một bản sao ở đây chỉ tạo ra hai nguồn sự thật, và bản ở đây sẽ là bản trôi.
>
> Ba thứ phải chuyển đi **trước khi xoá**, nếu lúc đó chưa có chỗ: giới hạn K7 xuyên process → ADR-023 ·
> lựa chọn A/B của R3 → plan M1 · phần R5 còn dở → mục tồn đọng của milestone kế tiếp.
> Xoá mà chưa chuyển thì đó là **giấu nợ**, không phải trả nợ.

## Trạng thái tại thời điểm audit

| Kiểm | Kết quả |
|---|---|
| `make ci` | 279/279 xanh |
| `make bus-fanout` | đạt — 1 publish → 2 queue độc lập |
| `make bus-dlq` | đạt — đúng 5 lần xử lý, queue chính 0, `_error` 1 |
| Mendix `.mpr` 11.12.3 | `mx.exe check` 0 lỗi |
| M1 D5 | **chưa đạt** — chủ repo chưa tự giải thích được |

> [!warning] Test xanh không phải bằng chứng các lỗi dưới đây không tồn tại
> Cả năm phát hiện đều nằm ở chỗ mà bộ test hiện có **không nhìn tới**. Đó là lý do chúng lọt qua
> hai milestone.

## Luật đọc bảng

| Cột | Nghĩa |
|---|---|
| **Trạng thái** | `xong` = đã commit · `đang làm` = code có trong working tree · `chưa` |
| **Đã tự kiểm** | Agent thực thi có **tự chạy lệnh xác minh lại** phát hiện của audit hay chưa. Chưa kiểm thì coi là **giả thuyết**, không phải sự thật |

| # | Vấn đề | Ràng buộc | Trạng thái | Đã tự kiểm |
|---|---|---|---|---|
| **R1** | IT đi vòng tới OT qua port host | **K11** | **xong** — `2a927a9` | ✅ đo lại 3 lần |
| **R2** | Idempotency không chặn duplicate đồng thời | **K7** | **xong** — commit R2 | ✅ lab phá hoại: bỏ cơ chế → **6/292** và **2/292** test đỏ, đúng chỗ, đúng lý do |
| **R3** | Activation revision 2 → 3 chưa được chứng minh | plan C08 | chưa | ☐ |
| **R4** | `IReadOnlyList` bị nhầm là immutable | — | chưa | ☐ |
| **R5** | Tài liệu nói M1 xong trong khi D5 còn mở | §1.3 | chưa | ☐ |

---

## R1 — Vi phạm K11: IT đi vòng tới OT qua host · **xong** `2a927a9`

**Phát hiện**: `it-net` không tới được `nvm-emqx:1883`, nhưng tới được `host.docker.internal:1883`.
Ranh giới network chỉ chặn đường trực tiếp; port publish ra host là cửa hông.

**Đã kiểm bằng số đo**: bind `127.0.0.1` **không** vá được — proxy Docker Desktop nối tới host qua
chính loopback. Container chỉ nằm trên network `internal: true` thì `ports:` bị Docker bỏ qua hoàn toàn.

**Đã sửa**: bỏ `ports:` của `emqx` · thêm `make dmz-shell` (jump host trên `dmz-net`) ·
`make net-check` → `scripts/net-check.sh`, 9 phép đo, exit ≠ 0 nếu thủng.

**Số đo đầy đủ**: `docs/plans/M0-bootstrap.md` §C08.4.

---

## R2 — Vi phạm K7: idempotency không chặn duplicate đồng thời · **xong**

**Phát hiện**: `IdempotencyBehavior` làm `Find → handler → Record`. Hai command cùng key tới cùng lúc
đều miss và đều chạy. `InMemoryIdempotencyStore` mất sạch key sau restart và không chia sẻ giữa hai
instance. Handler activation làm `Current → kiểm → Activate` không atomic.

**Mâu thuẫn nền**: K7 là ràng buộc **cứng**, trong khi plan hoãn transaction/persistence tới M5.

### Phần code đã làm (chưa commit)

| | |
|---|---|
| `IIdempotencyStore` | `Find`/`Record` → **`Claim`/`Complete`/`Abandon`**. Ba trạng thái: chưa thấy · đang bay · đã xong |
| `InMemoryIdempotencyStore` | Giành claim bằng một `TryAdd`; kẻ thua đợi `TaskCompletionSource<bool>` của kẻ thắng; timeout qua `TimeProvider`; chặn trùng key theo `commandType` **và** `ResultType` |
| `IdempotencyBehavior` | Claim **trước** handler; mọi nhánh lỗi → `Abandon`; cả hai nhánh settle dùng `CancellationToken.None` |
| `IActiveFactoryModel` | `Activate` → **`TryActivate(revision, expectedCurrentRevision)`** — compare-and-swap |
| Test | 13 test mới, `make ci` 292/292 |

**Hệ quả đáng ghi**: thứ tự Validation → Idempotency **giờ mới thật sự là correctness**. Trước đây đổi
chỗ hai stage không đổi hành vi gì. Claim-trước biến nó thành thật: command hỏng lọt xuống là chiếm mất
key, và bản gửi lại đã sửa bị nuốt như duplicate.

### Đã làm nốt — 2026-08-27

1. **Lab phá hoại**, hai lần, mỗi lần bỏ đúng một cơ chế rồi chạy lại cả 292 test:
   - **Lab A** — `InMemoryIdempotencyStore` về check-then-act: **6 đỏ / 292**, tất cả trong
     `IdempotencyConcurrencyTests`, mỗi test đỏ **đúng lý do** (bảng đối chiếu ở `ADR-023` §Evidence).
     **286 test còn lại xanh** — bộ test tuần tự của C05 *không thể* nhìn thấy lỗi này. Đó mới là con
     số đáng nhớ của lab.
   - **Lab B** — bỏ compare-and-swap trong `InMemoryActiveFactoryModel`: **2 đỏ / 292**, cả hai ở mức
     store (16/16 caller cùng thắng thay vì 1).
   - Hoàn nguyên bằng bản sao + `sha256sum -c`, khớp bit-for-bit. Không dùng `git checkout` (§1.2).
   - *Đoán trước khi đo* (§5.8.4): chủ repo đoán **≥ 10** và **≥ 7**, thực tế **6** và **2**. Chênh
     nằm ở chỗ dễ tưởng nhầm: bỏ một cơ chế **không** làm đỏ cả cụm test quanh nó. 4 test còn lại của
     `IdempotencyConcurrencyTests` canh **giới hạn** (hai instance không chia sẻ, guard trùng khoá)
     chứ không canh race, nên chúng sống sót — và toàn bộ test cũ cũng vậy.
2. **`ADR-023`** — *Giành chỗ trước khi chạy handler; K7 chỉ đúng trong một process cho tới M5*. Bảng
   index `docs/adr/README.md` đã thêm dòng, kèm ghi chú **ADR-022 và ADR-023 là một cặp** — cùng gốc
   *"M1 chưa có transaction nào"*, một cái trả giá ở đường ra, một cái ở đường vào.
3. Tài liệu: `oef-mapping.md` (dòng **Command Handler** — K7 **chưa đạt**, đua trong process đã đóng,
   đua xuyên process thì chưa) · `scope.md` §7.2 (check-then-act không đủ; chỗ giữ phải commit cùng
   effect) · `M1` §C05 (khối cảnh báo *"Sửa ở R2"* — sửa, không xoá) · `glossary.md` (**Claim**) ·
   `benchmarks.md` (7 dòng số đo).
4. **Chạy lặp**: 10 vòng × 13 test concurrency = **130/130 xanh**, thời gian từng vòng ghi đủ ở
   `benchmarks.md` và `ADR-023`. Cộng dồn 4.400 lượt dispatch tranh một khoá, 160 lượt activation
   tranh một plant, 0 lần handler chạy hai lần. `make ci` cuối: **292/292**.

> [!important] Phát hiện mới của lab B — chuyển sang R3, không tự sửa ở đây
> Lab B làm đỏ 2 test và **cả hai đều ở mức store**. Không test nào ở **mức handler** đỏ khi CAS bị bỏ,
> nghĩa là nhánh `FactoryModelActivationException` **chưa được test nào đi qua**.
>
> Đây là bằng chứng thực nghiệm cho đúng phát hiện của R3. Acceptance của R3 vì thế phải gồm cả nhánh
> **thua CAS đi qua handler**, không chỉ 2 → 3 thành công.

> [!important] Không được ghi K7 đạt
> Race trong một process đã đóng. Race xuyên process thì **chưa**, và không đóng được ở M1: effect duy
> nhất hiện có là một dictionary trong RAM, nên một claim bền vững canh một effect không bền vững thì
> **tệ hơn**, không tốt hơn. Đó là lý do không kéo SQL store từ M5 lên.

---

## R3 — Activation revision 2 → 3 chưa được chứng minh · **chưa**

**Phát hiện của audit** *(chưa tự kiểm — phải xác minh lại trước khi tin)*:

- Plan C08 **bắt buộc** có test *"kích hoạt revision 3 khi đang ở 2"*.
- Test hiện có chỉ bao gồm activation đầu tiên và kích hoạt lại revision 1.
- Snapshot được nạp **singleton lúc startup**; active revision nằm trong RAM và mất khi restart.
- Vì thế code **chưa chứng minh** staged rollout, cũng chưa chứng minh `EquipmentPathsAdded` /
  `EquipmentPathsRemoved` tính đúng giữa hai revision.

**Phải trình bày hai hướng và chờ chủ repo chọn — không được tự chọn**:

| | Hướng | Nghĩa là |
|---|---|---|
| **A** | M1 chỉ minh hoạ activation đầu tiên | Sửa plan/comment để **thôi tuyên bố** staged rollout đã chạy; dời transition thật sang milestone có persistence |
| **B** | M1 hỗ trợ transition thật | Thêm nguồn/catalog nhiều revision hoặc reload có kiểm soát, rồi test 2 → 3 và diff thêm/bớt |

**Không được giữ nguyên trạng**: plan yêu cầu một việc, code/test chứng minh một việc khác.

**Acceptance nếu chọn B**: active revision 2, candidate 3 · revision 3 kích hoạt thành công ·
added/removed đúng và **deterministic** · revision bằng hoặc thấp hơn bị từ chối · hai site ở hai
revision khác nhau được · có test cho restart/persistence theo boundary đã chọn · `make ci` xanh.

> Ghi chú: R2 đã thêm `ActiveFactoryModelConcurrencyTests` có phủ *"hai plant ở hai revision khác nhau"*
> ở mức store. Đó **không** thay thế được yêu cầu 2 → 3 đi qua handler.
>
> Và lab B của R2 đã **đo được** khoảng trống đó: bỏ hẳn compare-and-swap thì 2 test đỏ, cả hai ở mức
> store, **không một test nào ở mức handler đỏ**. Nhánh `FactoryModelActivationException` hiện chưa
> được test nào đi qua — thêm nó vào acceptance của R3, dù chọn hướng A hay B.

---

## R4 — Bịt lỗ immutability của factory model · **chưa**

**Phát hiện của audit** *(chưa tự kiểm)*:

- `FactoryNode.Create` **giữ nguyên** `IReadOnlyList` do caller truyền vào.
- `EquipmentPath.Segments` expose thẳng array nội bộ dưới dạng `IReadOnlyList`.
- `IReadOnlyList` **không** đồng nghĩa immutable: caller giữ lại tham chiếu gốc, hoặc cast ngược về
  `List<T>`, rồi mutate.
- Cây bị mutate **sau** khi snapshot đã dựng flat index → tree và index trả về **hai sự thật khác nhau**.

**Yêu cầu**: defensive-copy toàn bộ collection tại boundary · không expose object cast được về
collection mutable · **không thêm package** nếu BCL đủ.

**Test bắt buộc**: mutate source list sau `FactoryNode.Create` không làm node đổi · không mutate được
`Children`/`Segments` qua cast · flat index và tree luôn thống nhất.

**Commit message dự kiến**: `fix(factory-model): enforce immutable hierarchy collections`

---

## R5 — Đồng bộ tài liệu, giữ M1 ở trạng thái mở · **chưa**

Làm **sau** các repair unit code, để tài liệu phản ánh trạng thái cuối.

1. Bảo toàn phần sửa hash benchmark hợp lệ đang có.
2. **Không** ghi `status: done` khi D5 còn mở.
3. `scope.md` **không** được đánh dấu M1 xong / DoD đạt.
4. `docs/oef-mapping.md`: hai dòng **Bus-Centric Design** và **Manufacturing Service Bus** phải là
   `đang làm` cho tới khi chủ repo tự giải thích được.
5. C19 vẫn chưa hoàn thành.
6. **CloudEvents**: plan C12 phải khớp `ADR-008` và code — **6 header bắt buộc**, 5 thuộc tính tuỳ chọn
   vắng mặt khi chưa có nguồn, consumer đọc bằng extension theo nhu cầu, **không có**
   `CloudEventsConsumeFilter`. `scope.md` §7.4 phải phân biệt **transport header trên bus** với
   **full envelope trong event store/export**.
7. **Root README**: đổi `22 test` thành số hiện tại · đổi `5 readiness check` thành **6** và nêu
   `bus`/`rabbitmq` đúng thực tế.
8. **Mendix**: M0 plan dùng Studio Pro **11.12.3** · bỏ dòng nói model chưa commit · ghi commit Team
   Server `cc8c9db` · **không** tuyên bố đã fetch remote mới nếu chưa fetch.
9. Chạy link/path check phù hợp, `git diff --check`, `make ci`.

**Commit message dự kiến**: `docs(m1): align milestone status with verified implementation`

> [!caution] Điểm đã thay đổi so với lúc audit viết
> Chủ repo đã commit phần docs của **C19 chung vào commit R1** (`2a927a9`). Nghĩa là `scope.md` Phụ lục A
> hiện **đang** đánh dấu M1 `☑ ☑`, và `M1-factory-model-bus.md` đang `status: done (D5 còn mở)`.
> R5 vì thế là một commit **sửa lại**, không còn là *"đừng đánh dấu"*.

---

## Luật thi hành cho mọi repair unit

- Một lượt = **một** repair unit. Xong thì dừng, báo cáo, chờ chủ repo commit.
- Không `git commit` · `push` · `merge` · `rebase` · `tag` (`AGENTS.md` §1.1).
- Không `make down-v`, không dừng SQL Server / RabbitMQ / EMQX.
- Lab phá hoại: **hỏi chủ repo đoán con số trước**, rồi mới chạy (`AGENTS.md` §5.8.4).
- Thay đổi chạm nhiều file hoặc đổi contract → nêu đề xuất §2.3 và **chờ xác nhận trước khi code**.
- Thuật ngữ nghiệp vụ chưa có trong [`glossary.md`](glossary.md) → thêm **trước** khi dùng (§5.8.2).
- **Tự kiểm chứng lại phát hiện của audit bằng lệnh thật trước khi tin.** R1 đúng, nhưng một nhánh
  giải pháp mà audit gợi ý (`bind 127.0.0.1`) đã bị **số đo bác bỏ**. Audit là đầu mối, không phải phán quyết.
- **R5 xong thì chưa hết việc.** Bước cuối là **xoá chính file này** — xem khối cảnh báo ở đầu trang.
