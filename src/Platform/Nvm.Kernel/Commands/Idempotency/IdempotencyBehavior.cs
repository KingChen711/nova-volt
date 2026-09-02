namespace Nvm.Kernel.Commands.Idempotency;

/// <summary>Thực hiện một command đúng một lần, dù nó đến bao nhiêu lần và gần nhau đến đâu.</summary>
/// <typeparam name="TCommand">Command đang được xử lý.</typeparam>
/// <typeparam name="TResult">Kết quả trả về khi xử lý.</typeparam>
/// <param name="store">Nơi các claim được giữ và kết quả được ghi nhớ.</param>
/// <param name="clock">Đồng hồ duy nhất. Không bao giờ dùng <c>DateTimeOffset.UtcNow</c> (AGENTS.md K1).</param>
/// <remarks>
/// <para>
/// Đây là AGENTS.md K7 được hiện thực hoá. Thiết bị gửi lại khi không nhận được acknowledgement, một
/// gateway vừa hồi phục sẽ đẩy hết backlog của nó, và bus là at-least-once — nên cùng một ý định
/// (intention) đến hai hoặc ba lần là chuyện bình thường trong vận hành. Thiếu bước này, một bước công
/// đoạn bị hoàn tất hai lần và một lot vật liệu bị tiêu thụ hai lần, và các con số sai mà không để lại
/// dấu vết nào.
/// </para>
/// <para>
/// Một bản duplicate nhận lại <b>kết quả gốc được replay</b> chứ không phải một lỗi. Caller yêu cầu
/// một điều gì đó phải đúng; nó đúng; đó là thành công. Trả lời "đã làm rồi" như một thất bại sẽ buộc
/// mọi caller phải xử lý một tình huống bình thường như một exception.
/// </para>
/// <para>
/// <b>Claim trước, chạy sau.</b> Khoá được đặt trước khi handler được gọi, chứ không ghi lại sau khi
/// nó trả về. Ghi lại sau là kiểu check-then-act: hai command giống hệt nhau đến cùng một thời điểm
/// đều tra cứu, đều không thấy, và đều chạy. Một gateway đang đẩy backlog làm đúng như vậy — nó không
/// gửi lại lần lượt một cách lịch sự.
/// </para>
/// <para>
/// <b>Chính việc claim trước là điều khiến thứ tự behaviour trở thành yếu tố chịu tải
/// (load-bearing).</b> Từ đây trở đi, một command đến được bước này sẽ đánh dấu khoá của nó là đã bị
/// chiếm, nên validation <i>bắt buộc</i> phải nằm ngoài cùng: một command sai định dạng mà đi được
/// tới đây sẽ claim mất khoá, và bản gửi lại đã sửa đúng — mang cùng natural key — sẽ bị nuốt mất như
/// một bản duplicate. Người vận hành sửa lại form, bấm submit, thấy thành công, và không có gì xảy ra.
/// Xem <see cref="KernelServiceCollectionExtensions.AddNvmKernel"/>.
/// </para>
/// <para>
/// <b>Vẫn còn thiếu, và không phải thiếu nhỏ.</b> Claim chỉ sống được lâu bằng process. Qua một lần
/// restart, hoặc giữa hai instance, không có gì được chia sẻ và duplicate lọt qua. Để khắc phục cần
/// store là một database và claim phải commit trong cùng transaction với event mà nó bảo vệ
/// (docs/adr/ADR-023). Cho tới lúc đó, K7 chỉ đúng trong phạm vi một process chứ không hơn — đó là
/// điều đúng sự thật và cũng là điều mà test khẳng định.
/// </para>
/// </remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(IIdempotencyStore store, TimeProvider clock)
    : ICommandBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    private readonly IIdempotencyStore _store = store;
    private readonly TimeProvider _clock = clock;

    /// <summary>Số lượng bản duplicate mà instance này đã short-circuit. Dùng cho test và chẩn đoán.</summary>
    /// <remarks>
    /// Duplicate đáng để đếm dù chúng là chuyện bình thường: con số đi từ lác đác lên thành dồn dập
    /// chính là cách một cơn bão resend tự báo hiệu. Đây là một metric, không phải một audit entry —
    /// xem <see cref="Audit.AuditBehavior{TCommand, TResult}"/> để biết vì sao hai thứ đó khác nhau.
    /// </remarks>
    public int DuplicatesSuppressed => _duplicatesSuppressed;

    private int _duplicatesSuppressed;

    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TCommand command,
        CommandPipelineStep<TResult> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(continuation);

        var key = command.IdempotencyKey;
        var commandType = typeof(TCommand).Name;

        var claim = await _store
            .ClaimAsync<TResult>(key, commandType, cancellationToken)
            .ConfigureAwait(false);

        if (!claim.IsGranted)
        {
            Interlocked.Increment(ref _duplicatesSuppressed);

            return claim.Outcome.Result;
        }

        TResult result;
        try
        {
            result = await continuation().ConfigureAwait(false);
        }
        catch
        {
            // Mọi đường lỗi, kể cả cancellation. Một handler ném lỗi coi như chưa từng chạy, nên khoá
            // của nó phải trở lại trạng thái chưa từng thấy — nếu không một lần retry của lỗi tạm thời
            // sẽ bị nhầm thành duplicate và công việc bị mất vĩnh viễn.
            //
            // CancellationToken.None có chủ đích: token đưa ta tới đây có thể chính là token vừa bị
            // cancel, và một claim bị bỏ lửng — không hoàn tất cũng không abandon — sẽ chặn mọi
            // duplicate của command đó cho tới khi hết timeout.
            await _store.AbandonAsync(key, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        // Cũng dùng None. Ghi lại kết quả chính là điều giải phóng các caller đang chờ trên claim này;
        // abandon họ chỉ vì token bị cancel sau khi công việc đã xong sẽ khiến họ phải làm lại việc đã
        // thành công.
        await _store
            .CompleteAsync(key, result, _clock.GetUtcNow(), CancellationToken.None)
            .ConfigureAwait(false);

        return result;
    }
}
