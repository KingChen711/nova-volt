using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.UnitTests.Contracts;

public sealed class CloudEventEnvelopeTests
{
    [EventVersion(1)]
    private sealed record ProbeEvent(Guid EventId, DateTimeOffset OccurredAt, string SiteId) : IDomainEvent;

    private static readonly Guid KnownEventId = Guid.Parse("0198f3a1-7c2e-5b4d-9e11-3a7f2c9b0d44");

    private static readonly DateTimeOffset KnownTime =
        new(2026, 8, 25, 3, 15, 42, 128, TimeSpan.Zero);

    private static CloudEventEnvelope<ProbeEvent> BuildEnvelope() => new()
    {
        Data = new ProbeEvent(KnownEventId, KnownTime, "NV1"),
        Type = EventTypeName.Create("traceability", "unit-serialized", 1),
        Source = EventSource.Create("NV1", "app-execution"),
        Subject = "urn:trace-unit:cell:NV1CL16238A00123",
        CorrelationId = "WO-2026-0042",
        CausationId = "OPRUN-8891",
        PartitionKey = "NV1CL16238A00123",
    };

    [Fact]
    public void Id_IsReadFromThePayloadRatherThanStoredSeparately()
    {
        var envelope = BuildEnvelope();

        envelope.Id.ShouldBe(KnownEventId);
    }

    [Fact]
    public void Time_IsReadFromThePayloadRatherThanStoredSeparately()
    {
        var envelope = BuildEnvelope();

        envelope.Time.ShouldBe(KnownTime);
    }

    [Fact]
    public void Id_CannotDisagreeWithThePayloadEvenWhenTheDataIsReplaced()
    {
        // Đây chính là lý do Id được derive thay vì lưu riêng. Một biểu thức `with` thay payload sẽ
        // để lại một id cũ nếu envelope giữ bản sao riêng của nó — và một id cũ là một deduplication
        // key trỏ vào sai fact.
        var replacement = Guid.Parse("019a0000-0000-7000-8000-000000000001");
        var envelope = BuildEnvelope();

        var amended = envelope with { Data = envelope.Data with { EventId = replacement } };

        amended.Id.ShouldBe(replacement);
    }

    [Fact]
    public void SpecVersionAndContentType_AreFixed()
    {
        var envelope = BuildEnvelope();

        envelope.SpecVersion.ShouldBe("1.0");
        envelope.DataContentType.ShouldBe("application/json");
    }

    [Fact]
    public void SiteId_OnThePayload_AgreesWithTheSourceAndTheRoutingKey()
    {
        // Ba chuỗi cùng đặt tên cho một nhà máy theo ba cách viết khác nhau: NV1 trong payload, nv1
        // trong source URN, NV1 một lần nữa trong routing key. Đây là assertion rằng tất cả đều quy
        // về cùng một site code chuẩn.
        var envelope = BuildEnvelope();

        var route = RoutingKey.Create(envelope.Data.SiteId, envelope.Type);

        envelope.Source.SiteId.ShouldBe("NV1");
        route.SiteId.ShouldBe("NV1");
        envelope.Source.Value.ShouldBe("urn:novavolt:nv1:app-execution");
        route.Value.ShouldBe("nvm.NV1.traceability.unit-serialized.v1");
    }
}
