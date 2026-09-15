using System.Diagnostics.CodeAnalysis;

namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Điều gì đã xảy ra lần đầu tiên một command mang khoá này được xử lý.</summary>
/// <typeparam name="TResult">Kết quả command đã trả về.</typeparam>
/// <param name="Result">Kết quả tạo ra lúc đó, được replay lại cho mọi bản duplicate đến sau.</param>
/// <param name="FirstHandledAt">Lúc bản gốc được xử lý.</param>
public sealed record IdempotentOutcome<TResult>(TResult Result, DateTimeOffset FirstHandledAt);

/// <summary>Câu trả lời cho "tôi có được phép xử lý command này không?".</summary>
/// <typeparam name="TResult">Kết quả command trả về.</typeparam>
/// <remarks>
/// Hai câu trả lời và không có câu thứ ba. Hoặc caller giờ đang giữ claim và phải hoàn tất nó, hoặc
/// việc đã xong rồi và đây là kết quả nó tạo ra. Một caller đến trong lúc người khác đang giữ claim sẽ
/// hoàn toàn chưa nhận được câu trả lời cho tới khi người đó xong việc — xem
/// <see cref="IIdempotencyStore.ClaimAsync{TResult}"/>.
/// </remarks>
public sealed class IdempotencyClaim<TResult>
{
    /// <summary>Dựng một claim.</summary>
    /// <param name="outcome">
    /// Kết quả lần xử lý đầu tiên tạo ra, hoặc null để cấp claim cho caller. Nên dùng các factory có
    /// tên trên <see cref="IdempotencyClaim"/>, vì chúng nói rõ đây là trường hợp nào trong hai
    /// trường hợp.
    /// </param>
    public IdempotencyClaim(IdempotentOutcome<TResult>? outcome) => Outcome = outcome;

    /// <summary>Kết quả lần xử lý đầu tiên tạo ra, nếu có.</summary>
    public IdempotentOutcome<TResult>? Outcome { get; }

    /// <summary>True khi caller phải tự làm việc; false khi nó phải replay lại <see cref="Outcome"/>.</summary>
    [MemberNotNullWhen(false, nameof(Outcome))]
    public bool IsGranted => Outcome is null;
}

/// <summary>Đặt tên cho hai câu trả lời, để call site đọc lên đúng nghĩa chúng mang.</summary>
/// <remarks>
/// Cố ý không dùng generic. Các factory không thể đặt trên chính <see cref="IdempotencyClaim{TResult}"/>
/// — static member trên một generic type buộc mọi caller phải viết rõ type argument ra, đó chính là
/// điều CA1000 nói tới.
/// </remarks>
public static class IdempotencyClaim
{
    /// <summary>Caller giờ đang giữ claim và phải gọi complete hoặc abandon.</summary>
    /// <typeparam name="TResult">Kết quả command trả về.</typeparam>
    public static IdempotencyClaim<TResult> Granted<TResult>() => new(null);

    /// <summary>Việc đã được làm xong rồi; replay lại kết quả này.</summary>
    /// <typeparam name="TResult">Kết quả command trả về.</typeparam>
    /// <param name="outcome">Kết quả lần xử lý đầu tiên tạo ra.</param>
    public static IdempotencyClaim<TResult> Replay<TResult>(IdempotentOutcome<TResult> outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return new IdempotencyClaim<TResult>(outcome);
    }
}

/// <summary>Cho phép đúng một caller thực hiện một command, dù có bao nhiêu caller cùng hỏi.</summary>
/// <remarks>
/// <para>
/// <b>Một giao thức claim, không phải một lookup.</b> Hình dạng hiển nhiên nhất — tra khoá, chạy
/// handler, ghi khoá lại — là một check-then-act, và check-then-act không sống sót được qua
/// concurrency: hai command giống hệt nhau đến cùng một thời điểm đều tra, đều không thấy, và đều
/// chạy. Dedupe mà chỉ đúng khi không có gì xảy ra cùng lúc thì không phải là dedupe. Vì vậy quyết
/// định và việc giữ chỗ (reservation) xảy ra trong <see cref="ClaimAsync{TResult}"/>, trong một bước
/// duy nhất, và kết quả được ghi lại sau đó.
/// </para>
/// <para>
/// <b>Ba trạng thái, không phải hai.</b> Một khoá là chưa thấy, <i>đang bay (in flight)</i>, hoặc đã
/// hoàn tất. Trạng thái ở giữa là trạng thái mà hình dạng cũ không có chỗ chứa, và cũng là trạng thái
/// khiến concurrency hoạt động đúng.
/// </para>
/// <para>
/// <b>Người giữ claim luôn phải hoàn tất nó.</b> Thành công thì gọi
/// <see cref="CompleteAsync{TResult}"/>; thất bại thì gọi <see cref="AbandonAsync"/>. Một claim không
/// được complete cũng không được abandon sẽ chặn mọi bản duplicate của command đó cho tới khi hết
/// timeout — đó là lý do đường xử lý thất bại dùng <see cref="CancellationToken.None"/> thay vì token
/// vừa có thể bị cancel.
/// </para>
/// <para>
/// <b>Abandon giải phóng khoá thay vì ghi nhớ thất bại.</b> Một handler đã ném exception coi như chưa
/// từng xảy ra, nên một lần retry phải được phép chạy. Nếu ghi nhớ lại thất bại đó, một trục trặc
/// thoáng qua của database sẽ biến thành mất dữ liệu vĩnh viễn trong im lặng: lần gửi lại bị nhầm
/// thành duplicate và việc đó không bao giờ được làm.
/// </para>
/// <para>
/// Generic theo kết quả thay vì lưu các object rời rạc, để một implementation cần serialize — bản SQL
/// Server, đến cùng event store — có thể làm việc đó khi biết trước type, thay vì phải đoán lúc chạy.
/// </para>
/// <para>
/// Timestamp là một tham số thay vì thứ store tự đọc từ đồng hồ. Truyền nó vào giữ đồng hồ ở một chỗ
/// duy nhất, nơi một test có thể chỉnh nó (AGENTS.md K1).
/// </para>
/// <para>
/// Store SQL C05 giữ claim, effect của handler và outcome <b>trong cùng một transaction</b>.
/// Store RAM chỉ dùng cho command Development có effect trong RAM. Đây là ranh giới của implementation,
/// không phải lời hứa mọi store đều tồn tại qua restart. Publish broker vẫn ngoài SQL transaction;
/// xem docs/adr/ADR-022 và ADR-023.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>Giữ chỗ khoá này để xử lý, hoặc trả lại kết quả lần xử lý đầu tiên đã tạo ra.</summary>
    /// <typeparam name="TResult">Kết quả command trả về.</typeparam>
    /// <param name="key">Idempotency key của command.</param>
    /// <param name="commandType">Tên command, giữ lại để chẩn đoán và cho việc canh gác (guard) khi lệch loại.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    /// <returns>
    /// <see cref="IdempotencyClaim.Granted{TResult}"/> khi caller phải tự làm việc, hoặc một bản replay
    /// của kết quả trước đó. Khi một caller khác đang giữ claim, lệnh này sẽ chờ caller đó xong việc:
    /// nếu thành công thì kết quả được replay lại, và nếu caller đó bỏ cuộc thì claim lại được tranh
    /// chấp lần nữa.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Khoá này trước đó đã được claim bởi một command khác hoặc một result type khác. Hai command
    /// cùng suy ra một natural key là lỗi modelling ở phía trên, và điều duy nhất đáng làm với nó là
    /// nói to lên chứ không phải replay một kết quả sai kiểu vào code không liên quan.
    /// </exception>
    Task<IdempotencyClaim<TResult>> ClaimAsync<TResult>(
        IdempotencyKey key,
        string commandType,
        CancellationToken cancellationToken);

    /// <summary>Người giữ claim đã thành công. Ghi lại kết quả và giải phóng mọi caller đang chờ.</summary>
    /// <typeparam name="TResult">Kết quả command trả về.</typeparam>
    /// <param name="key">Idempotency key của command.</param>
    /// <param name="result">Kết quả việc xử lý tạo ra.</param>
    /// <param name="handledAt">Lúc nó được xử lý, lấy từ đồng hồ hệ thống.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    Task CompleteAsync<TResult>(
        IdempotencyKey key,
        TResult result,
        DateTimeOffset handledAt,
        CancellationToken cancellationToken);

    /// <summary>Người giữ claim đã thất bại. Bỏ claim để một lần retry có thể lấy nó.</summary>
    /// <param name="key">Idempotency key của command.</param>
    /// <param name="cancellationToken">Cancellation cho toàn bộ thao tác.</param>
    Task AbandonAsync(IdempotencyKey key, CancellationToken cancellationToken);
}
