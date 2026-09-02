using System.Globalization;
using Npgsql;

namespace Nvm.Ingestion;

/// <summary>Giới hạn runtime và endpoint database duy nhất mà ingestion bridge được phép dùng.</summary>
public sealed class IngestionOptions
{
    /// <summary>Configuration section mà service này đọc.</summary>
    public const string SectionName = "NVM_INGEST";

    private const int DefaultPostgresPort = 5432;

    /// <summary>Connection string PostgreSQL. Cấp qua environment trong container.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Địa chỉ bên trong container. Không publish host port nào.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8080";

    /// <summary>Giới hạn cứng trước khi protobuf decode cấp phát object graph.</summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Số reading tối đa chấp nhận trong một transaction.</summary>
    public int MaxReadingsPerBatch { get; set; } = 50_000;

    /// <summary>Số batch được phép giữ một database transaction cùng lúc.</summary>
    /// <remarks>
    /// Admission control, không phải một thiết lập throughput. Không có trần, một gateway đang xả
    /// hàng giờ backlog sẽ mở transaction nhanh hơn PostgreSQL đóng chúng lại, và thất bại sẽ đến
    /// dưới dạng connection-pool timeout rải khắp mọi caller thay vì một tiếng "hãy chậm lại" trung
    /// thực.
    /// </remarks>
    public int MaxConcurrentBatches { get; set; } = 8;

    /// <summary>Số batch được phép chờ một slot trước khi ingestion bắt đầu từ chối.</summary>
    public int MaxQueuedBatches { get; set; } = 16;

    /// <summary>Số database writer mà một batch được chấp nhận có thể trải các dòng của nó ra.</summary>
    /// <remarks>
    /// <para>
    /// PostgreSQL phục vụ một connection bằng một backend process, nên một batch được ghi qua một
    /// transaction đơn chỉ dùng được đúng một core dù host có bao nhiêu core đi nữa. Đo được ở R5:
    /// với một edge node và một flusher tuần tự nghiêm ngặt, toàn bộ pipeline chạy serial, container
    /// timescale ngồi ở mức ~93% của một core, và throughput đầu-cuối dừng lại ở 4.141 msg/s trong
    /// khi gateway đã chấp nhận 5.105 msg/s. Tuning memory không thay đổi được gì vì giới hạn nằm ở
    /// CPU, không phải cache miss.
    /// </para>
    /// <para>
    /// Một nhà máy production đạt được cùng mức parallelism đó miễn phí, nhờ có nhiều edge node cùng
    /// post một lúc. D2 chỉ đo một edge node, nên việc fan-out phải được khai báo rõ ràng ở đây.
    /// </para>
    /// <para>
    /// Mỗi writer commit transaction riêng của nó, nên một thất bại có thể để lại một phần batch đã
    /// commit. Điều đó an toàn chính xác vì dedup là hợp đồng: gateway retry cả batch và các dòng đã
    /// commit quay lại dưới dạng duplicate thay vì bản sao thứ hai. Không cái nào từng bị mất và
    /// không cái nào từng bị lưu hai lần, đó chính là điều D1 khẳng định.
    /// </para>
    /// </remarks>
    public int WriterParallelism { get; set; } = 4;

    /// <summary>Số dòng một batch phải vượt qua trước khi đáng để chia ra nhiều writer.</summary>
    public int MinRowsPerWriter { get; set; } = 256;

    /// <summary>Delay trả về trong <c>Retry-After</c> khi một batch bị từ chối.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Device clock được phép lệch với clock của gateway bao xa trước khi một reading bị flag.</summary>
    public TimeSpan ClockDriftThreshold { get; set; } = ClockQualityClassifier.DefaultThreshold;

    /// <summary>Thư mục chứa các tài liệu factory-model bất biến.</summary>
    public string SeedDirectory { get; set; } = "seed";

    /// <summary>Revision mà service này đọc; null chọn tài liệu đã publish mới nhất.</summary>
    public int? Revision { get; set; }

    /// <summary>Adapter file-drop CSV (C15). Tắt trừ khi nhà máy có một máy cũ.</summary>
    public FileDrop.FileDropOptions FileDrop { get; set; } = new();

    /// <summary>Nơi giữ các byte gốc của một export được drop (C12). Tắt theo mặc định.</summary>
    public RawCurves.RawCurveArchiveOptions RawCurveArchive { get; set; } = new();

    /// <summary>Signal code chỉ ra một kết quả đã đánh giá thay vì một observation.</summary>
    /// <remarks>
    /// Rỗng nghĩa là chỉ có telemetry, đó là mặc định an toàn: reading vẫn được lưu dù thế nào. Liệt
    /// kê code mà nhà máy dùng cho <b>kết quả</b> — <c>Formation/CapacityResult</c>, không phải
    /// <c>Formation/Capacity</c> — vì danh sách này là thứ duy nhất nói rằng một signal mang theo
    /// một quyết định. Xem <see cref="Publishing.PublishedSignals"/> để biết ranh giới từ scope.md §5.5.
    /// </remarks>
    public IList<string> PublishedSignals { get; } = [];

    /// <summary>Host RabbitMQ. Chỉ được tiếp cận từ chân <c>it-net</c> của service này.</summary>
    public string BusHost { get; set; } = "rabbitmq";

    /// <summary>Port AMQP.</summary>
    public ushort BusPort { get; set; } = 5672;

    /// <summary>User của broker. Không bao giờ là <c>guest</c>.</summary>
    public string BusUsername { get; set; } = string.Empty;

    /// <summary>Password của broker.</summary>
    public string BusPassword { get; set; } = string.Empty;

    /// <summary>Process này có nên kết nối tới bus hay không.</summary>
    /// <remarks>
    /// False khi không có result signal nào được whitelist hoặc thiếu credentials. Một migration job
    /// không có cả hai, và một bus mà nó không bao giờ dùng tới là một dependency chỉ có thể gây fail.
    /// </remarks>
    public bool PublishesToBus =>
        PublishedSignals.Count > 0
        && !string.IsNullOrWhiteSpace(BusUsername)
        && !string.IsNullOrWhiteSpace(BusPassword);

    /// <summary>Xây options, dùng các biến PostgreSQL riêng của repo cho local development.</summary>
    public static IngestionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new IngestionOptions();
        configuration.GetSection(SectionName).Bind(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            options.ConnectionString = BuildDevelopmentConnectionString(configuration);
        }

        options.Validate();
        return options;
    }

    /// <summary>Fail trước khi bind một socket, cho trường hợp service chỉ có thể fail ở mọi request.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(ListenUrl);

        if (MaxRequestBytes <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxRequestBytes must be positive.");
        }

        if (MaxReadingsPerBatch <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxReadingsPerBatch must be positive.");
        }

        if (MaxConcurrentBatches <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxConcurrentBatches must be positive.");
        }

        if (MaxQueuedBatches < 0)
        {
            throw new InvalidOperationException($"{SectionName}:MaxQueuedBatches cannot be negative.");
        }

        if (WriterParallelism <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:WriterParallelism must be positive.");
        }

        if (MinRowsPerWriter <= 0)
        {
            throw new InvalidOperationException($"{SectionName}:MinRowsPerWriter must be positive.");
        }

        if (RetryAfter <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:RetryAfter must be positive.");
        }

        if (ClockDriftThreshold < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{SectionName}:ClockDriftThreshold cannot be negative.");
        }

        if (Revision is < 1)
        {
            throw new InvalidOperationException($"{SectionName}:Revision must be at least 1.");
        }

        FileDrop.Validate();
        RawCurveArchive.Validate();
    }

    private static string BuildDevelopmentConnectionString(IConfiguration configuration)
    {
        var portText = configuration["NVM_PORT_POSTGRES"];
        var port = string.IsNullOrWhiteSpace(portText)
            ? DefaultPostgresPort
            : int.Parse(portText, NumberStyles.None, CultureInfo.InvariantCulture);

        return new NpgsqlConnectionStringBuilder
        {
            Host = configuration[$"{SectionName}:DatabaseHost"] ?? "localhost",
            Port = port,
            Database = Required(configuration, "NVM_POSTGRES_DB"),
            Username = Required(configuration, "NVM_POSTGRES_USER"),
            Password = Required(configuration, "NVM_POSTGRES_PASSWORD"),
            ApplicationName = "Nvm.Ingestion",
        }.ConnectionString;
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key]
        ?? throw new InvalidOperationException(
            $"Configuration '{key}' is missing. Copy .env.example to .env or set {SectionName}:ConnectionString.");
}
