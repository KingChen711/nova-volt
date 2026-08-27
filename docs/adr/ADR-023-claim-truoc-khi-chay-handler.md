# ADR-023 — Giành chỗ trước khi chạy handler; K7 chỉ đúng trong một process cho tới M5

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-27 |
| **Liên quan** | `ADR-010` (khoá dedup), `ADR-001` (event store ở M5), `ADR-022` (dual-write), [`AGENTS.md`](../../AGENTS.md) §4/K7, [`scope.md`](../scope.md) §7.2, [`plans/M1-factory-model-bus.md`](../plans/M1-factory-model-bus.md) §C05, §C08, [`audit-m0-m1.md`](../audit-m0-m1.md) R2 |

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
Race xuyên process vẫn mở, và **cố ý** để mở tới M5 — lý do ở §Alternatives, dòng đầu tiên.

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
- Nhánh "thua CAS" **chưa có test ở mức handler**: lab B làm đỏ 2 test, cả hai ở mức store. Ghi lại cho
  R3 trong `audit-m0-m1.md`.
- Không có eviction. Đúng cho test, không chấp nhận được cho một process chạy dài.

**Việc phát sinh**

- **M5**: bản SQL Server, `Claim` và event ghi trong **cùng một transaction**; test cho restart và cho
  hai instance. Đây mới là chỗ K7 đóng lại.
- **M2**: đo chi phí `Claim` dưới tải thật (≥ 5.000 msg/s), cùng lúc với chi phí SHA-1 của `ADR-010`.
- **R3**: chứng minh transition revision 2 → 3 **đi qua handler**, gồm cả nhánh thua CAS.
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

Máy đo: Windows 11, .NET 10, cấu hình `Release`. HEAD `2a927a9`, cây làm việc R2 chưa commit.

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

Không test nào ở **mức handler** đỏ. Nhánh `FactoryModelActivationException` chưa được test nào đi qua
— ghi lại ở đây để R3 nhặt, thay vì lặng lẽ bỏ.

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
