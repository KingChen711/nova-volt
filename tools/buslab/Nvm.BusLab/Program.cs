using System.Globalization;
using Nvm.Bus;
using Nvm.BusLab;
using Nvm.BusLab.Consumers;
using Nvm.Hosting;

// Cùng lý do như Nvm.Host.All: định dạng nhất quán bất kể locale của máy, mà không cần
// InvariantGlobalization. Xem ADR-020.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = Host.CreateApplicationBuilder(args);

// Một nguồn sự thật duy nhất cho port và credential: cùng file .env mà docker-compose đọc.
if (builder.Environment.IsDevelopment())
{
    DotEnvLoader.Load(builder.Environment.ContentRootPath);
}

builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
    options.UseUtcTimestamp = true;
});

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddNvmBus(
    bus =>
    {
        bus.Port = ushort.Parse(DotEnvLoader.Required("NVM_PORT_RABBITMQ"), CultureInfo.InvariantCulture);
        bus.Username = DotEnvLoader.Required("NVM_RABBITMQ_USER");
        bus.Password = DotEnvLoader.Required("NVM_RABBITMQ_PASSWORD");
        // Công cụ đo này không bao giờ publish, nên tên chỉ đi tới log. Nó vẫn được khai báo: một
        // application name chỉ được điền vào nơi nó được dùng là cái tên sẽ sai vào đúng ngày có thứ
        // gì đó bắt đầu publish.
        bus.ApplicationName = "bus-lab";
    },
    consumers =>
    {
        // Hai registration, hai receive endpoint, hai queue. Đăng ký cả hai trên một endpoint thì
        // mỗi message chỉ tới đúng một trong hai — competing consumer, không phải fan-out, và sự
        // khác biệt này không lộ ra cho tới khi ai đó nhận thấy audit trail thiếu mất một nửa.
        if (LabConsumerSelection.Includes(LabConsumerSelection.Cache))
        {
            consumers.AddNvmConsumer<MeasurementCacheConsumer>();
        }

        if (LabConsumerSelection.Includes(LabConsumerSelection.Audit))
        {
            consumers.AddNvmConsumer<MeasurementAuditConsumer>();
        }

        // Tắt trừ khi được yêu cầu. Xem LabConsumerSelection.
        if (LabConsumerSelection.Includes(LabConsumerSelection.Failing))
        {
            consumers.AddNvmConsumer<FailingMeasurementConsumer>();
        }
    });

var host = builder.Build();

var startup = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Nvm.BusLab");

// Đọc trước lời gọi: Describe() truy cập environment, và CA1873 đúng khi cho rằng một argument làm
// điều đó bên trong một lời gọi log sẽ phải trả giá bất kể dòng log có được ghi ra hay không.
var roles = LabConsumerSelection.Describe();

StartupLog.Starting(startup, roles);

// Một công cụ đo cố tình fail mọi message đáng để có một warning thay vì một dòng chìm giữa những
// tiếng ồn lúc khởi động — ai đó lỡ để switch bật muốn được báo ngay, chứ không phải phát hiện ra
// từ một error queue vào ngày mai.
if (LabConsumerSelection.Includes(LabConsumerSelection.Failing))
{
    StartupLog.FailingConsumerIsOn(startup);
}

await host.RunAsync();
