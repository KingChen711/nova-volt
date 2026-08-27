# ADR-025 — Collection lộ ra ngoài dùng `ImmutableArray<T>`, không dùng `IReadOnlyList<T>`

| | |
|---|---|
| **Status** | **Accepted** |
| **Date** | 2026-08-27 |
| **Liên quan** | `ADR-024` (revision là tài liệu), [`AGENTS.md`](../../AGENTS.md) §4/K4, [`scope.md`](../scope.md) §5.7, [`audit-m0-m1.md`](../audit-m0-m1.md) R4 |

---

## Context

`FactoryNode`, `FactoryModelSnapshot` và `EquipmentPath` đều tự mô tả là **read-only**, và XML doc của
chúng nói đúng tinh thần: đổi nhà máy là kích hoạt revision mới, không sửa node tại chỗ. Nhưng kiểu
dùng để diễn đạt điều đó là `IReadOnlyList<T>`, và **`IReadOnlyList<T>` không có nghĩa là bất biến**.

Nó chỉ hứa rằng *tham chiếu này* không có mutator. Đối tượng đứng sau nó thì vẫn có.

Audit R4 nêu hai chỗ; kiểm chứng lại bằng test khai thác thật cho **năm** chỗ, cả năm đều chạy được:

1. `FactoryNode.Create` **giữ nguyên** collection của caller. Caller thêm phần tử sau khi `Create` trả
   về → node đổi theo, và **các invariant vừa kiểm xong bị vô hiệu**: node thêm vào không đi qua phép
   kiểm "path con phải là path cha cộng một đoạn".
2. `node.Children` ép được về `IList<FactoryNode>` rồi `Add`.
3. `path.Segments` ép được về `string[]` rồi ghi thẳng. Sau đó `Value` nói một đằng còn `SiteId`,
   `Code`, `Kind` nói một nẻo — một path tự nhận là hai máy khác nhau tuỳ member được hỏi.
4. `snapshot.Sites` ép được về `IList<FactorySite>` rồi `Clear`.
5. Sửa cây **sau khi** snapshot đã dựng flat index → **cây và index vĩnh viễn không khớp**: duyệt cây
   thấy 42 node, index nói 41, và `Find` trả `null` cho một node đang nằm trong cây.

Điểm 5 là cái đắt nhất. Flat index là đường tra cứu của **mọi** message từ sàn nhà máy
(`scope.md` §2.1). Hai nguồn sự thật lệch nhau mà **không có gì ném exception**: telemetry không gắn
được vào equipment nào, và triệu chứng chỉ lộ ra hàng tháng sau dưới dạng dữ liệu thiếu chứ không phải
lỗi.

**Phát hiện phụ, và nó mới là lý do phải đổi kiểu chứ không chỉ vá vài chỗ**: hai chỗ khác —
`FactoryModelRevisionActivated.EquipmentPathsAdded` và `IFactoryModelCatalog.Revisions` — **đang an
toàn**, nhưng an toàn *do tình cờ*. Cả hai được gán bằng collection expression, nên compiler sinh ra
`<>z__ReadOnlyList` và mọi mutator ném `NotSupportedException`. Đo được bằng `GetType().FullName` lúc
chạy. Nghĩa là mức an toàn phụ thuộc vào **cách viết ở một call site**, không phụ thuộc hợp đồng kiểu:
đổi một dòng thành `List<string>` là lỗ mở lại, im lặng, và không test nào hiện có bắt được.

## Decision

Mọi collection **lộ ra ngoài** của domain model dùng **`ImmutableArray<T>`**. Đầu vào nhận
`IEnumerable<T>` và **sao chép tại boundary**, trước khi kiểm invariant.

Cụ thể: `EquipmentPath.Segments` · `FactoryNode.Children` · `FactoryModelSnapshot.Sites` ·
`FactoryModelSnapshot.Paths` · `IFactoryModelCatalog.Revisions`.

Flat index của snapshot dùng **`FrozenDictionary`** — dựng một lần lúc nạp, đọc trên mọi message, và
không còn mutator nào để với tới bằng cast.

Chỉ dùng BCL. `System.Collections.Immutable` và `System.Collections.Frozen` nằm sẵn trong shared
framework của .NET 10, **không thêm package** nào.

**Ranh giới**: quyết định này áp cho **domain model**, không áp cho `FactoryModelRevisionActivated`.
Event là **wire contract** có golden file và chịu `NVM002`; đổi kiểu ở đó là một quyết định riêng, có
rủi ro riêng ở serialization, và phải là một đơn vị công việc riêng. Cái đã làm được ngay: hàm sinh ra
hai danh sách đó trả `ImmutableArray<string>`, nên bảo đảm đến **từ kiểu** chứ không từ cách viết —
kiểu khai báo trên contract vẫn là `IReadOnlyList<string>`.

## Consequences

**Được**

- Đường ép kiểu về mảng bị chặn **lúc biên dịch**: `(string[])path.Segments` giờ là `error CS0030`. Đây
  là mức bảo đảm mạnh nhất có thể có — không cần chạy mới biết.
- Đường ép về `IList<T>` vẫn biên dịch được (ImmutableArray cố tình cài `IList<T>` để truyền được vào
  API cũ) nhưng **mọi mutator ném `NotSupportedException`**.
- Defensive copy trở thành **tường minh và bắt buộc**: `Create` sao chép trước khi kiểm, nên không còn
  cảnh "kiểm một thứ, giữ một thứ khác".
- `FrozenDictionary` nhanh hơn khi tra cứu, đúng hình dạng dùng thật: ghi một lần, đọc rất nhiều lần.
- Kiểu nói ra ý định. `ImmutableArray<T>` trong chữ ký là một câu tuyên bố, `IReadOnlyList<T>` là một
  lời gợi ý.

**Mất / phải chịu**

- **`default(ImmutableArray<T>)` là cái bẫy thật.** Nó không phải mảng rỗng — nó là bọc quanh `null`, và
  dùng tới là `NullReferenceException` ở chỗ chẳng liên quan. Ở đây mọi đường gán đều đi qua
  `ToImmutableArray()` hoặc collection expression nên không tạo ra được bản `default`, nhưng luật này
  phải nhớ khi thêm kiểu mới.
- **Đổi API công khai**: `Count` thành `Length` ở mọi caller, và `Create` nhận `IEnumerable<T>`.
- **Sao chép lúc dựng**. Ở 41–46 node thì không đáng kể; ở quy mô thật, một máy formation có ~1.000
  kênh (`scope.md` §2.4), nên chi phí dựng snapshot sẽ phải đo lại ở M5.
- Bảo đảm này chủ yếu do **compiler** giữ, mà compiler thì im lặng biến mất nếu ai đó nới kiểu về
  `IReadOnlyList` cho một chữ ký gọn hơn. Vì thế phải có một test phản-hồi-quy soi kiểu khai báo bằng
  reflection — nó là thứ duy nhất bắt được loại thay đổi đó (xem §Evidence, đối chứng dương).
- Contract của event vẫn là `IReadOnlyList<string>`, tức vẫn còn một chỗ trên bề mặt công khai nơi bảo
  đảm đến từ cách viết. Đã ghi ra thay vì để im.

**Việc phát sinh**

- Cân nhắc analyzer `NVM004` ép quy ước này, để nó cùng hạng với `NVM001`–`NVM003` thay vì chỉ có một
  test canh. Test hiện tại phải liệt kê từng member; analyzer thì không.
- Entity của M5 phải theo cùng quy ước ngay từ đầu.
- Nếu đổi kiểu trên event contract: đơn vị công việc riêng, kèm kiểm golden file.
- Đo lại chi phí dựng snapshot khi seed đạt quy mô thật.

## Alternatives considered

| Phương án | Vì sao loại |
|---|---|
| **Giữ `IReadOnlyList<T>`, ghi rõ trong XML doc** | Đã làm rồi, và đã hỏng: doc của `FactoryNode` **đang** nói "Read-only" trong khi năm đường khai thác đều chạy được. Một bảo đảm mà compiler không giữ thì không phải bảo đảm |
| `ReadOnlyCollection<T>` bọc quanh list | Chặn được lúc chạy (mutator ném) nhưng **không chặn lúc biên dịch**, vẫn ép về `IList<T>` được, và tốn thêm một lớp bọc cho mỗi node. Được ít hơn, trả nhiều hơn |
| Trả `IEnumerable<T>` | Chặn ghi, nhưng lấy mất `Length` và indexer — mà `Children[0]` và `Segments[^1]` là cách dùng chính. Và caller sẽ `.ToList()` khắp nơi, tức lại tạo ra bản sao mutable |
| Chỉ vá `FactoryNode.Create` (defensive copy), giữ nguyên kiểu | Bịt được lỗ 1, để nguyên lỗ 2–5. Và không trả lời được vấn đề "an toàn do tình cờ" ở §Context |
| Thêm thư viện immutable ngoài BCL | `System.Collections.Immutable` đã nằm trong shared framework .NET 10. Audit R4 nói thẳng: **không thêm package nếu BCL đủ** |

## Evidence

Máy đo: Windows 11, .NET 10, cấu hình `Release`. HEAD `0015a05`, cây làm việc R4 chưa commit.

**Trước** — năm test khai thác, viết để **chạy được**, và cả năm **xanh**:

| Khai thác | Kết quả trước khi sửa |
|---|---|
| Sửa list đã truyền vào `FactoryNode.Create` | Node đổi theo: `Children.Count` 1 → 2 |
| `((IList<FactoryNode>)node.Children).Add(...)` | Thành công |
| `((string[])path.Segments)[1] = "DE1"` | Thành công; `path.SiteId` thành `DE1` trong khi `Value` vẫn chứa `NV1` |
| `((IList<FactorySite>)snapshot.Sites).Clear()` | Thành công; snapshot còn 0 plant |
| Thêm node vào cây sau khi index đã dựng | Duyệt cây **42**, `NodeCount` **41**, `Find` trả `null` cho node vừa thêm |

Ghi chú đo được, và nó là lý do phải đổi kiểu chứ không vá từng chỗ: một trong năm khai thác lúc đầu
**trượt** — không phải vì `FactoryNode` an toàn, mà vì test đó truyền children bằng collection
expression, nên compiler sinh ra một list read-only. Đổi đúng dòng đó thành `new List<FactoryNode>`
là khai thác chạy. An toàn phụ thuộc cách caller viết, không phụ thuộc kiểu.

Cùng phép đo cho hai chỗ ngoài phạm vi audit, bằng `GetType().FullName` lúc chạy:

```
added runtime type     = <>z__ReadOnlyList`1[[System.String, ...]]   → mutator ném NotSupportedException
revisions runtime type = <>z__ReadOnlyList`1[[System.Int32, ...]]    → mutator ném NotSupportedException
```

**Sau**:

- `(string[])path.Segments` không còn biên dịch: `error CS0030: Cannot convert type
  'System.Collections.Immutable.ImmutableArray<string>' to 'string[]'`.
- Bốn khai thác còn lại lật thành test khẳng định `NotSupportedException` và khẳng định cây và index
  luôn khớp.
- `make ci`: **319 / 319** xanh (309 → +10).

**Đối chứng dương** — nới đúng **một** kiểu (`IFactoryModelCatalog.Revisions`) về `IReadOnlyList<int>`:

```
Total: 10, Failed: 1
EveryCollectionOnThePublicSurface_IsDeclaredImmutable(
    declaringType: typeof(IFactoryModelCatalog), memberName: "Revisions") [FAIL]
  IFactoryModelCatalog.Revisions must stay an ImmutableArray
```

Đúng một test đỏ, nêu đích danh member. **Các test mutation lúc chạy vẫn xanh** trong đối chứng đó —
vì giá trị được gán vẫn là một `ImmutableArray` đã bị đóng hộp. Đó là bằng chứng rằng test soi kiểu
khai báo không thừa: nó là cái duy nhất bắt được việc nới kiểu, và việc nới kiểu là cách lỗ này sẽ mở
lại. Hai file đã hoàn nguyên, `sha256sum -c` khớp.
