namespace Nvm.Ingestion.FileDrop;

/// <summary>Nơi file được thả tới và nơi chúng đi tiếp sau đó.</summary>
public sealed class FileDropOptions
{
    /// <summary>Thư mục mà một máy cũ ghi các file export của nó vào.</summary>
    public string InboxPath { get; set; } = "/var/lib/nvm-ingestion/inbox";

    /// <summary>Nơi một file đi tới khi mọi dòng của nó đã được xử lý xong.</summary>
    public string ProcessedPath { get; set; } = "/var/lib/nvm-ingestion/processed";

    /// <summary>Nơi các dòng bị từ chối và các file không đọc được đi tới, mỗi cái kèm một file <c>.error</c> bên cạnh.</summary>
    public string RejectedPath { get; set; } = "/var/lib/nvm-ingestion/rejected";

    /// <summary>Bao lâu thì inbox được kiểm tra một lần.</summary>
    /// <remarks>
    /// Polling thay vì dùng filesystem watcher. Inbox thường là một share SMB hoặc NFS, và thông báo
    /// thay đổi trên các share đó không đáng tin cậy theo đúng cách gây hại nhất: chúng bị bỏ lỡ một
    /// cách âm thầm, nên một file cứ nằm đó và không ai biết nó chưa được xử lý cho tới khi một báo
    /// cáo bị thiếu số liệu.
    /// </remarks>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tên mà một file export hoàn tất được đổi thành, và cũng là toàn bộ hợp đồng publish.</summary>
    /// <remarks>
    /// <para>
    /// Một producer ghi file export dưới một tên tạm, đóng file lại, rồi đổi tên nó một cách atomic
    /// thành <c>&lt;name&gt;.csv.ready</c>. Việc đổi tên đó chính là publish: trước đó file thuộc về
    /// exporter, sau đó file đã hoàn tất và adapter này có thể lấy nó. Không có gì khác trong inbox
    /// từng được đọc.
    /// </para>
    /// <para>
    /// Vì sao trạng thái sẵn sàng nằm trong chính tên của file export thay vì trong một file marker
    /// bên cạnh: reader phải nhận dữ liệu và trạng thái sẵn sàng của nó trong đúng một bước. Hai file
    /// nghĩa là hai lần đổi tên, và một producer publish lại đúng tên đó giữa hai lần đổi tên sẽ trao
    /// cho reader byte của export này dưới trạng thái sẵn sàng của một export khác. Không có thứ tự
    /// nào của hai thao tác đó khép được lỗ hổng này — được đo đạc trên một image thật, không phải
    /// suy luận (<c>ADR-035</c> §Evidence).
    /// </para>
    /// <para>
    /// Vì sao cơ chế này là cần thiết: đổi tên một file ra khỏi inbox không đóng handle mà exporter
    /// của nó vẫn còn giữ. Trên NFS, exporter vẫn tiếp tục append vào cùng inode đó sau khi đổi tên,
    /// nên ingestion lưu lại phần đầu của một lượt ghi còn phần đuôi bị xoá cùng với claim, không có
    /// lỗi nào hiện ra ở đâu cả. Chỉ producer mới biết file đã hoàn tất; việc đổi tên là cách nó nói
    /// điều đó.
    /// </para>
    /// Hậu tố bắt buộc có giá trị: thời gian file im lặng không chứng minh exporter đã đóng file.
    /// <c>ADR-036</c> bỏ chế độ đoán bằng mtime; cấu hình rỗng phải bị từ chối trước khi nhận file.
    /// </remarks>
    public string PublishedSuffix { get; set; } = ".ready";

    /// <summary>Một file được phép nằm trong inbox mà chưa publish bao lâu trước khi watcher lên tiếng.</summary>
    /// <remarks>
    /// Đây là cái giá của một hợp đồng fail-closed: một exporter chưa từng được dạy đổi tên file
    /// export của nó vào đúng chỗ sẽ làm rớt những file không bao giờ được đọc, và "không bao giờ
    /// được đọc" thì không có lỗi riêng của nó. Cơ chế này làm cho việc chờ đợi trở nên ồn ào thay vì
    /// im lặng, mỗi file một lần, để một exporter cấu hình sai được phát hiện qua việc đọc log thay
    /// vì qua việc nhận ra một báo cáo bị thiếu một ca sản xuất.
    /// </remarks>
    public TimeSpan UnpublishedWarningAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Adapter file-drop có chạy hay không.</summary>
    public bool Enabled { get; set; }

    /// <summary>Từ chối một cấu hình mà sẽ không thể xử lý được bất kỳ file nào.</summary>
    /// <exception cref="ArgumentException">Thiếu path hoặc hậu tố publish.</exception>
    /// <exception cref="InvalidOperationException">Khoảng thời gian, hậu tố hoặc quan hệ giữa các thư mục không hợp lệ.</exception>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(InboxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ProcessedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(RejectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(PublishedSuffix);

        if (PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The poll interval must be positive.");
        }

        ValidatePublishedSuffix();

        if (UnpublishedWarningAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The unpublished-file warning delay must be positive, or a file waiting for a "
                + "publish that will never come waits without anybody being told.");
        }

        if (string.Equals(InboxPath, ProcessedPath, StringComparison.Ordinal)
            || string.Equals(InboxPath, RejectedPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The inbox must differ from the processed and rejected directories, or a file moved "
                + "out of it would be picked up again on the next poll and stored a second time — "
                + "which deduplication would swallow, leaving a loop nobody can see.");
        }
    }

    private void ValidatePublishedSuffix()
    {
        if (PublishedSuffix.Length < 2 || PublishedSuffix[0] != '.')
        {
            throw new InvalidOperationException(
                $"'{PublishedSuffix}' is not a published-name suffix. It must begin with '.' and "
                + "name something, so a finished export reads as 'export.csv.ready'.");
        }

        if (PublishedSuffix.AsSpan(1).ContainsAny(Path.GetInvalidFileNameChars()))
        {
            throw new InvalidOperationException(
                $"'{PublishedSuffix}' cannot be part of a file name on this platform.");
        }

        if (string.Equals(PublishedSuffix, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The published-name suffix cannot be '.csv': a published export would then be "
                + "indistinguishable from one an exporter is still writing, which is the whole "
                + "thing the suffix exists to tell apart.");
        }
    }
}
