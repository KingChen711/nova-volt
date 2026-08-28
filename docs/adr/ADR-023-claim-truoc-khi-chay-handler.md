# ADR-023 — Giành chỗ trước khi chạy handler; K7 chỉ đúng trong một process cho tới khi có store bền vững

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-27 |
| **Liên quan** | `ADR-010` (khoá dedup), `ADR-001` (event store ở M5), `ADR-022` (dual-write), [`AGENTS.md`](../../AGENTS.md) §4/K7, [`scope.md`](../scope.md) §7.2, [`plans/M1-factory-model-bus.md`](../plans/M1-factory-model-bus.md) §C05, §C08 |

---

## Context

K7 là ràng buộc **cứng**: *"mọi command handler idempotent, vì bus là at-least-once"*. Cài đặt từ
C05 tới hết M1 không đạt điều đó.

**Hình dạng cũ** — `IdempotencyBehavior` gọi `FindAsync` → chạy handler → `RecordAsync`. Đó là
check-then-act: khoá chỉ được ghi **sau khi** handler trả về, nên hai bản của cùng một command tới
cùng lúc đều tra trượt, đều được cho qua, và đều chạy. Cùng loại lỗi ở
`ActivateFactoryModelRevisionHandler`: `Current` → kiểm → `Activate` là ba bước, và có thể xen vào giữa.

**Vì sao nó không lộ ra suốt hai milestone**: bộ test lúc đó chỉ gửi trùng **tuần tự**. Một cơ chế
dedup chỉ đúng khi không có gì xảy ra đồng thời thì không phải dedup — nhưng không có test nào nhìn
tới chỗ đó. Xem §Evidence: hoàn nguyên về check-then-act làm đỏ **6** test, và **286** test còn lại
vẫn xanh.

**Ràng buộc thật lúc quyết**: M1 **không có database nào**. `ADR-001` đặt event store ở M5. Effect duy
nhất mà một command ở M1 tạo ra là một `ConcurrentDictionary` trong RAM. Không có transaction nào để
một khoá dedup cùng commit với thứ nó bảo vệ.

**Cái gì bị chặn nếu không quyết**: M2 là *Simulator, Ingestion & Idempotency*. Nó dựng tầng dedup thứ
hai **trên giả định** rằng tầng command handler đã idempotent (`scope.md` §7.2, khối *"Dedup ở đâu là
đủ?"*). Để nguyên nền sai thì M2 xây tiếp lên trên nó.

**Nghiệp vụ đứng sau**: edge gateway ở khu `FORMATION` làm store-and-forward. Mất mạng thì nó đệm
xuống đĩa; sống lại thì nó **xả cả đệm cùng lúc**, không gửi lại lịch sự từng cái một. Đó chính xác là
kịch bản check-then-act không chịu nổi, và nó xảy ra mỗi lần mạng OT chớp.

## Decision

**Giành chỗ trước, chạy sau.** Ba phần, một quyết định:

1. `IIdempotencyStore` đổi từ `Find`/`Record` sang **`Claim`/`Complete`/`Abandon`**. Một khoá có **ba**
   trạng thái — *chưa thấy* · *đang bay* · *đã xong* — thay vì hai. Trạng thái giữa là thứ hình dạng cũ
   không có chỗ để diễn đạt.
2. `IdempotencyBehavior` giành chỗ **trước** khi gọi handler. Mọi nhánh lỗi, kể cả cancellation, gọi
   `Abandon`; cả hai nhánh settle dùng `CancellationToken.None`.
3. `IActiveFactoryModel.Activate` đổi thành **`TryActivate(revision, expectedCurrentRevision)`** —
   compare-and-swap, lấy chính số revision làm version token.

**Ranh giới của quyết định này**: nó **không** tuyên bố K7 đã đạt. Nó đóng race **trong một process**.
Race xuyên process vẫn mở, và **cố ý** để mở cho tới khi có một effect bền vững để commit cùng —
lý do ở §Alternatives, dòng đầu tiên. Lúc viết, mốc đó là M5; từ 2026-08-30 nó là **M4** (xem
*"Điều kiện kéo lên ĐÃ xảy ra"*).

## Consequences

**Được**

- Race trong một process đóng lại, và có số đo chứng minh (§Evidence).
- `Abandon` làm nhánh thất bại đúng: handler ném thì coi như chưa xảy ra, khoá trở lại *chưa thấy*, bản
  gửi lại được phép chạy. Nhớ lấy thất bại sẽ biến một cú chớp mạng thành **mất việc vĩnh viễn và im
  lặng**.
- Thứ tự `Validation → Idempotency` từ preference thành **correctness**. Trước đây đổi chỗ hai stage
  không đổi hành vi gì; giờ đổi chỗ là bug thật.
- `Claim`/`Complete`/`Abandon` chính là API mà bản SQL Server cần. M5 thay **cài đặt**, không thay
  contract.
- CAS ở `FactoryModel` là bài tập nhỏ của `expectedVersion` mà event store sẽ dùng ở M5, gặp trước ở
  chỗ rẻ tiền hơn nhiều.

**Mất / phải chịu**

- **K7 vẫn CHƯA ĐẠT.** Chỗ giữ sống đúng bằng đời của process. Restart, hoặc hai instance sau load
  balancer, thì bản trùng lọt qua như cũ. Đây là điều phải nói ra ở mọi chỗ nhắc tới K7, không phải một
  chú thích cuối trang.
- Caller thua **phải chờ** caller thắng. Một handler treo giữ luôn mọi bản trùng của command đó. Giảm
  nhẹ bằng timeout 30 s có tên command trong thông điệp lỗi — nhưng **30 s là con số đoán, chưa đo**.
- Handler activation giờ ném `FactoryModelActivationException` khi thua CAS. Caller phải đọc lại
  revision hiện tại và quyết lại; retry mù sẽ thua mãi.
- Một command **hỏng** lọt xuống tới stage này sẽ chiếm mất khoá, và bản gửi lại **đã sửa** — mang cùng
  natural key — bị nuốt như bản trùng. Người vận hành sửa form, bấm gửi, thấy thành công, và không có
  gì xảy ra. Đây là giá của claim-trước, và là lý do `ValidationBehavior` phải ở ngoài cùng.
- Nhánh "thua CAS" ném `FactoryModelActivationException`, và nhánh đó **đã được test đi qua ở mức
  handler**: `ActivateFactoryModelRevisionTests.Activating_WhenThePlantMovedUnderneath_IsRefusedRatherThanOverwriting`
  ép thua bằng một stand-in `IActiveFactoryModel`, nên nhánh chạy trên **mọi** build chứ không chỉ trên
  build mà scheduler tình cờ xen kẽ đúng cách.
- Không có eviction. Đúng cho test, không chấp nhận được cho một process chạy dài.

**Việc phát sinh**

- **M4**: `IIdempotencyStore` bền vững phải có mặt trong hoặc trước commit đầu tiên có write — xem mục
  *"Điều kiện kéo lên ĐÃ xảy ra"* dưới đây. Đây là chỗ K7 đóng cho effect của M4.
- **M5**: bản SQL Server, `Claim` và event ghi trong **cùng một transaction**; test cho restart và cho
  hai instance. Đây là chỗ K7 đóng cho event store.

### Lịch đóng K7 — chốt 2026-08-29, không còn để ngỏ

`AGENTS.md` K7 nói *"mọi command handler idempotent"* như một ràng buộc **cứng**, còn repo đang chạy
`InMemoryIdempotencyStore`. Hai câu đó mâu thuẫn nhau, và cho tới lúc này mâu thuẫn ấy chỉ sống trong
đầu người đọc. Đây là chỗ nó được ghi ra:

| | |
|---|---|
| **Đóng ở** | ~~**M5**, cùng event store~~ — **đã bị thay bởi mục ngay dưới bảng này (2026-08-30)**. Lập luận gốc vẫn đúng nguyên văn: một chỗ giữ bền vững canh một effect **không** bền vững thì tệ hơn chứ không tốt hơn. Nó chỉ không còn áp dụng, vì effect của M4 **là** bền vững |
| **Kéo lên trước M4 nếu** | M4 tạo **effect bền vững** (ghi xuống DB, gửi ra ngoài) **hoặc** chạy nhiều instance. Lúc đó điều kiện "effect không bền vững" không còn đúng và lý do hoãn biến mất |
| **Nếu M4 chỉ là demo một process** | Permanent docs phải nói thẳng giới hạn đó — chính là mục này — chứ không im lặng |
| **Điều kiện nghiệm thu ở M5** | `Claim` có unique key trong SQL; claim + business effect + outcome **cùng một transaction**; replay bản trùng trả về **đúng outcome cũ**; và production host **từ chối khởi động** với in-memory store |

#### Điều kiện kéo lên ĐÃ xảy ra — cập nhật 2026-08-30 (J6 của re-audit)

Bảng trên viết điều kiện kéo lên ở thì tương lai (*"kéo lên trước M4 **nếu** M4 tạo effect bền vững"*),
và đọc lại `scope.md` §9/M4 thì điều kiện đó **đã đúng ngay lúc bảng này được viết**. M4 có:

- *"Submit data collection → thấy event xuất hiện trên RabbitMQ management UI"* — một effect rời khỏi
  process và không lấy lại được.
- *"Gọi lại đúng command với cùng `idempotencyKey` → server trả kết quả cũ, **không** tạo bản ghi thứ
  hai"* — một DoD **không thể** đạt bằng `InMemoryIdempotencyStore`: chỗ giữ sống đúng bằng đời của
  process, nên một lần restart giữa hai lần gửi là một bản ghi thứ hai, im lặng.

Nên nhánh *"nếu M4 chỉ là demo một process"* không còn áp dụng, và không được tiếp tục viết *"đóng ở
M5"* như thể điều kiện chưa xảy ra.

| | |
|---|---|
| **Phải có trong hoặc trước commit đầu tiên của M4 có write** | Một `IIdempotencyStore` **bền vững**: `Claim` có unique key ở tầng database, và **outcome được lưu** để replay trả lại đúng kết quả cũ thay vì chạy lại handler. Claim + effect nghiệp vụ + outcome commit **cùng một transaction** |
| **Đường nào KHÔNG được đi** | Không kéo event store của M5 về sớm — cái M4 cần là một bảng claim/outcome, không phải một stream store. Không Redis, không distributed lock: cả hai nằm ngoài transaction ghi effect nên chỉ đổi một lỗ lấy một lỗ (xem §Alternatives) |
| **Nghiệm thu ở M4** | Gửi lại cùng `idempotencyKey` **sau khi restart process** → trả đúng outcome cũ, `count(*)` của bản ghi nghiệp vụ **không đổi**; và host từ chối khởi động với in-memory store |
| **Cái gì KHÔNG nằm trong transaction đó** | **Publish lên RabbitMQ.** Transaction của M4 bao **claim + bản ghi nghiệp vụ + outcome** — cả ba đều trong cùng một database. Việc bắn event lên bus vẫn nằm ngoài nó, nên đó vẫn là **dual-write** đúng như `ADR-022` mô tả, và nó chỉ đóng lại khi outbox có ở **M6**. Idempotency của handler không sửa được chuyện đó: nó bảo đảm handler chạy một lần, không bảo đảm event rời khỏi process |
| **M5 vẫn giữ phần của mình** | Bản chạy cùng event store: claim + **event** trong một transaction, và test cho hai instance. M4 đóng K7 cho effect của M4; M5 đóng nó cho event store |

Không cần Redis, distributed lock, saga engine hay lời hứa exactly-once từ broker. M2 đã đóng tầng
**ingestion** của K7 — khoá dedup commit cùng transaction với chính row nó bảo vệ (C12) — và đó là
tầng khác với tầng command handler. `scope.md` §7.2 gọi đúng: **hai tầng, hai lịch**.
- **M2**: đo chi phí `Claim` dưới tải thật (≥ 5.000 msg/s), cùng lúc với chi phí SHA-1 của `ADR-010`.
- Đo lại ngưỡng timeout 30 s khi có tải thật; hiện tại nó là phán đoán.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Kéo store SQL Server từ M5 lên M1** | Một chỗ giữ **bền vững** canh một effect **không bền vững** thì tệ hơn, không tốt hơn: restart xong, khoá vẫn nằm trong database còn effect đã bốc hơi cùng RAM — lệnh gửi lại bị nuốt vĩnh viễn và không ai biết. Chỗ giữ chỉ có nghĩa khi nó commit cùng thứ nó bảo vệ, và thứ đó chưa tồn tại |
| Giữ check-then-act, ghi *"known gap"* rồi đi tiếp | Gap đó không phải trường hợp hiếm — nó là **kịch bản thường ngày** của store-and-forward. Và M2 sẽ dựng tầng dedup thứ hai lên trên giả định tầng này đúng |
| Lock toàn cục theo khoá, bọc quanh handler | Cùng hiệu quả trong một process, nhưng không phân biệt được *đang bay* với *đã xong*, nên caller thua không có kết quả để replay — nó phải chạy lại. Và không có đường đi lên bản phân tán |
| Redis / distributed lock | Thêm hạ tầng thứ bảy chỉ để phục vụ một ràng buộc mà M5 giải được bằng transaction đã có sẵn. Lock phân tán mà không nằm trong transaction ghi effect thì vẫn là dual-write (`ADR-022`) — đổi một lỗ lấy một lỗ |
| Đợi tới M5 mới sửa cả gói | Chi phí sửa contract `IIdempotencyStore` tăng theo số handler dùng nó. M1 có **một**; M5 sẽ có nhiều |

## Evidence

Máy đo: Windows 11, .NET 10, cấu hình `Release`. Số đo lấy tại thời điểm ra quyết định (2026-08-27, nền `2a927a9`); giữ nguyên ở đây làm **bằng chứng của quyết định**, không phải mô tả cây hiện tại.

**Nền** — `make ci`:

```
total: 292 · failed: 0 · succeeded: 292 · exit 0
```

**Lab phá hoại A** — hoàn nguyên `InMemoryIdempotencyStore` về check-then-act (tra khoá → chạy handler
→ ghi khoá, không giữ chỗ), giữ nguyên mọi thứ khác:

```
total: 292 · failed: 6 · succeeded: 286
```

Sáu test đỏ, tất cả trong `IdempotencyConcurrencyTests`, và mỗi test đỏ **đúng lý do**:

| Test | Kỳ vọng | Thực tế khi check-then-act |
|---|---|---|
| `EightCallersOneKey_FiftyRoundsRunning_NeverHandleTwice` | `gate.Invocations` = 1 | **7** |
| `ThirtyTwoCallersOneKey_RunTheHandlerOnceAndAllReceiveTheSameResult` | `InFlightCount` = 1 | **0** — không còn trạng thái *đang bay* để đếm |
| `ConcurrentCallersWhenTheHandlerThrows_AllFailAndTheKeyIsLeftFreeForARetry` | `gate.Invocations` = 2 | **8** |
| `SecondClaimWhileTheFirstIsInFlight_WaitsAndReplaysTheFirstResult` | claim thứ hai chưa hoàn thành | hoàn thành ngay |
| `SecondClaimWhileTheFirstIsInFlight_IsGrantedWhenTheFirstGivesUp` | claim thứ hai chưa hoàn thành | hoàn thành ngay |
| `ClaimHeldWithoutSettling_TimesOutAndNamesTheCommandThatIsStuck` | ném `TimeoutException` | không ném |

**286 test còn lại xanh** — kể cả toàn bộ `CommandPipelineTests`, nơi có phép kiểm K7 tuần tự của C05.
Đó là con số đắt nhất của lab này: bộ test cũ **không thể** nhìn thấy lỗi, vì nó chưa bao giờ gửi hai
bản cùng lúc.

**Lab phá hoại B** — bỏ compare-and-swap trong `InMemoryActiveFactoryModel.TryActivate` (ghi đè vô điều
kiện, luôn trả `true`):

```
total: 292 · failed: 2 · succeeded: 290
```

| Test | Kỳ vọng | Thực tế khi ghi đè vô điều kiện |
|---|---|---|
| `SixteenActivationsAtOnce_ExactlyOneWins` | 1 caller thắng | **16** |
| `ActivatingOnAStaleRead_IsRefusedRatherThanOverwriting` | `false` | `true` |

Cả hai test đỏ nằm ở **mức store**. Điều đó nói rằng một lab chỉ chứng minh được thứ có test đứng đúng
tầng để nhìn: nhánh `FactoryModelActivationException` ở **mức handler** cần một test riêng, và giờ nó
có — `Activating_WhenThePlantMovedUnderneath_IsRefusedRatherThanOverwriting`, ép thua bằng stand-in.

**Chạy lặp** — 10 vòng, mỗi vòng 13 test concurrency, trên cây đã hoàn nguyên:

| Vòng | Kết quả | Thời gian |
|---|---|---|
| 1 | 13/13 xanh | 0,650 s |
| 2 | 13/13 xanh | 0,480 s |
| 3 | 13/13 xanh | 0,462 s |
| 4 | 13/13 xanh | 0,477 s |
| 5 | 13/13 xanh | 0,465 s |
| 6 | 13/13 xanh | 0,456 s |
| 7 | 13/13 xanh | 0,479 s |
| 8 | 13/13 xanh | 0,504 s |
| 9 | 13/13 xanh | 0,502 s |
| 10 | 13/13 xanh | 0,473 s |

Cộng dồn: **4.400 lượt dispatch** tranh nhau một khoá (320 lượt của test 32-caller + 4.000 lượt của test
8-caller qua 500 vòng nội bộ + 80 lượt bão lỗi) và **160 lượt activation** tranh một plant. **0 lần**
handler chạy hai lần.

Con số này chứng minh *"không thấy race trong 10 vòng"*, **không** chứng minh *"không có race"*. Một
test concurrency xanh một lần chỉ nói rằng một cách xen kẽ đã ổn. Bằng chứng dứt khoát duy nhất trong
tài liệu này là **lab phá hoại**: bỏ cơ chế đi thì test đỏ, đúng chỗ, đúng lý do.

**Hoàn nguyên**: hai file lab được khôi phục từ bản sao và đối chiếu bằng `sha256sum -c` — khớp
bit-for-bit. Không dùng `git checkout` ([`AGENTS.md`](../../AGENTS.md) §1.2).
