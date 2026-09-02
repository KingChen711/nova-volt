using Nvm.Sparkplug;

namespace Nvm.Simulator.Publishing;

/// <summary>Nơi các message của nhà máy đi tới.</summary>
/// <remarks>
/// Một interface để những gì simulator <i>tạo ra</i> có thể được test mà không cần broker. Câu hỏi
/// "nén thời gian có làm thay đổi measurement hay không" là câu hỏi về việc sinh dữ liệu và không liên
/// quan gì tới MQTT, và một test cần một EMQX đang chạy để trả lời câu hỏi đó sẽ hiếm khi được chạy và
/// ít được tin tưởng hơn.
/// </remarks>
public interface ISparkplugPublisher : IAsyncDisposable
{
    /// <summary>Mở session. Được gọi một lần trước khi bất kỳ thứ gì được publish.</summary>
    /// <param name="cancellationToken">Hủy việc connect.</param>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Publish một message. Fail thay vì chờ khi không có session.</summary>
    /// <param name="message">Topic và payload.</param>
    /// <param name="cancellationToken">Hủy việc publish.</param>
    /// <exception cref="SparkplugPublishException">Đường truyền đang down, hoặc bị down giữa chừng lúc gửi.</exception>
    Task PublishAsync(SparkplugMessage message, CancellationToken cancellationToken);

    /// <summary>Chờ cho tới khi có một session để publish lên đó.</summary>
    /// <param name="cancellationToken">Dừng việc chờ.</param>
    /// <remarks>
    /// <para>
    /// Tách riêng khỏi <see cref="PublishAsync"/> vì hai việc này thuộc về hai chỗ khác nhau, và gộp
    /// chúng vào một chỗ là một deadlock. Một publish mà chờ sẽ giữ caller của nó lại giữa chừng một
    /// batch — và caller đó đang giữ lock mà node cần để tự khai báo lại chính nó trên session mà nó
    /// đang chờ.
    /// </para>
    /// <para>
    /// Vậy nên việc chờ nằm phía trên lock đó, còn publish thì fail nhanh phía dưới lock đó. Cái fail
    /// chỉ là một batch, được soạn dưới một session đã kết thúc; batch kế tiếp được soạn dưới session
    /// mới, sau các birth.
    /// </para>
    /// </remarks>
    Task WaitForSessionAsync(CancellationToken cancellationToken);

    /// <summary>Được gọi khi một host yêu cầu node này tự khai báo lại chính nó.</summary>
    /// <remarks>
    /// <para>
    /// Được set bởi worker trước khi connect. Một consumer bỏ lỡ các birth — nó subscribe trễ một
    /// giây, hoặc nó restart — không thể đọc dù chỉ một message alias-only sau đó, và không có gì tự
    /// phục hồi được: <c>DBIRTH</c> kế tiếp cách đó một lần đổi cell, mà trên một formation line là
    /// mười tám giờ. Câu trả lời của Sparkplug là <c>NCMD Node Control/Rebirth</c>, và một device bỏ
    /// qua nó sẽ khiến consumer đó bị mù cho phần còn lại của run.
    /// </para>
    /// <para>
    /// Là một property chứ không phải một event vì handler là bất đồng bộ và chỉ có đúng một handler.
    /// Một event sẽ mời gọi hai subscriber cùng republish các birth giống nhau cùng lúc, đây chính là
    /// điều duy nhất mà một rebirth không được phép làm.
    /// </para>
    /// </remarks>
    Func<CancellationToken, Task>? RebirthRequested { get; set; }

    /// <summary>Cung cấp <c>bdSeq</c> cho một session mà publisher này sắp mở.</summary>
    /// <remarks>
    /// Last will được đăng ký tại CONNECT và không thể thay đổi sau đó, nên con số này phải được biết
    /// trước khi socket mở - đó là lý do publisher tự lấy số này thay vì được báo cho biết.
    /// </remarks>
    Func<ulong>? BeginSession { get; set; }

    /// <summary>Được gọi sau khi một connection bị rớt đã được thiết lập lại.</summary>
    /// <remarks>
    /// Một reconnect khiến mọi consumer giữ alias table và một counter <c>seq</c> thuộc về một session
    /// đã kết thúc, nên node phải tự khai báo lại chính nó. Đây là công việc giống hệt một rebirth;
    /// điều khác biệt là ai đã yêu cầu.
    /// </remarks>
    Func<CancellationToken, Task>? SessionRestored { get; set; }
}
