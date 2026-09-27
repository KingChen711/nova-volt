using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nvm.EventStore;

/// <summary>Span và metric của event store. Tên có tiền tố "NovaVolt." để host bật bằng AddNvmObservability.</summary>
public static class EventStoreTelemetry
{
    public const string SourceName = "NovaVolt.EventStore";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    // Mặc định của SDK là thang mili-giây (0, 5, 10, 25… ) nên với đơn vị giây mọi mẫu rơi vào một bucket; ngưỡng
    // cảnh báo 0,5 s cần bucket quanh đó.
    private static readonly InstrumentAdvice<double> Seconds = new()
    { HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 10, 30, 60] };

    /// <summary>Thời gian một lần publish tới broker (gồm chờ broker xác nhận). Broker chậm thì số này tăng trước tiên.</summary>
    public static readonly Histogram<double> PublishDuration = Meter.CreateHistogram<double>(
        "nvm.outbox.publish.duration", "s", "Thời gian publish một event từ outbox tới broker.", advice: Seconds);

    /// <summary>Từ lúc event được ghi tới lúc broker nhận: độ trễ mà consumer thấy.</summary>
    public static readonly Histogram<double> DeliveryLag = Meter.CreateHistogram<double>(
        "nvm.outbox.delivery.lag", "s", "Thời gian từ khi ghi event tới khi broker nhận.", advice: Seconds);
}
