using System.Text.Json;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events;

namespace Nvm.Kernel.EventSourcing;

/// <summary>Dựng bản ghi event store từ một domain event có contract, cùng metadata CloudEvents.</summary>
public static class DomainEventRecord
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="fact">Domain event có <c>[EventContract]</c> và <c>[EventVersion]</c>.</param>
    /// <param name="subject">URN của đối tượng nghiệp vụ, ví dụ <c>urn:trace-unit:cell:NV1CL...</c>.</param>
    /// <param name="partitionKey">Khoá giữ thứ tự trên bus, thường là site + đối tượng.</param>
    /// <param name="recordedAt">Thời điểm hệ thống ghi nhận, từ <see cref="TimeProvider"/>.</param>
    /// <param name="correlationId">Chuỗi nghiệp vụ gốc; mặc định là chính event.</param>
    /// <param name="causationId">Event trực tiếp gây ra event này; mặc định là chính nó.</param>
    public static NewStreamEvent Create(IDomainEvent fact, string subject, string partitionKey,
        DateTimeOffset recordedAt, string? correlationId = null, Guid? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        var type = EventTypeName.Of(fact.GetType());
        var payload = JsonSerializer.Serialize(fact, fact.GetType(), Json);
        var metadata = JsonSerializer.Serialize(new
        {
            subject,
            correlationid = correlationId ?? fact.EventId.ToString(),
            causationid = (causationId ?? fact.EventId).ToString(),
            partitionkey = partitionKey
        }, Json);
        return new NewStreamEvent(fact.EventId, type.Value, type.Version, payload, metadata,
            fact.OccurredAt, recordedAt);
    }
}
