using Nvm.ProductionExecution.Commands;

namespace Nvm.UnitTests.Commands;

public sealed class RecordDataCollectionMappingTests
{
    [Fact]
    public void SubmissionKeyMatchesIndependentUuidV5Vector()
        => RecordDataCollectionCommand.KeyFor("NV1", "b7e2f0a1-3c4d-4e5f-a6b7-c8d9e0f1a2b3").Value
            .ShouldBe(Guid.Parse("e2a083f7-28f0-50c3-93f9-6977165c2339"));

    [Fact]
    public void MissingClientKeyUsesTheSameServerDerivedSubmissionKey()
    {
        var request = Request(401.25m);
        RecordDataCollectionCommandFactory.TryCreate(request with { IdempotencyKey = null },
            "NV1", "operator", out var omitted, out var omittedError).ShouldBeTrue();
        omittedError.ShouldBe(RecordDataCollectionMappingError.None);
        RecordDataCollectionCommandFactory.TryCreate(request with { IdempotencyKey = "" },
            "NV1", "operator", out var empty, out var emptyError).ShouldBeTrue();
        emptyError.ShouldBe(RecordDataCollectionMappingError.None);
        omitted!.IdempotencyKey.ShouldBe(empty!.IdempotencyKey);
        omitted.IdempotencyKey.Value.ToString("D").ShouldBe(request.IdempotencyKey);
        RecordDataCollectionCommandFactory.TryCreate(request with { IdempotencyKey = "wrong" },
            "NV1", "operator", out _, out var malformed).ShouldBeFalse();
        malformed.ShouldBe(RecordDataCollectionMappingError.MalformedKey);
    }

    [Fact]
    public void CanonicalPayload_NormalizesTimeOffsetAndEquivalentDecimal()
    {
        var request = Request(401.250m);
        RecordDataCollectionCommandFactory.TryCreate(request, "NV1", "operator", out var first, out _).ShouldBeTrue();
        var equivalent = request with { OccurredAt = request.OccurredAt.ToOffset(TimeSpan.FromHours(7)), Payload = request.Payload with { Value = 401.25m } };
        RecordDataCollectionCommandFactory.TryCreate(equivalent, "NV1", "operator", out var second, out _).ShouldBeTrue();
        first!.CanonicalPayload.ShouldBe(second!.CanonicalPayload);
    }

    [Fact]
    public void CanonicalPayload_CoversEveryFrozenBusinessField()
    {
        var request = Request(401.25m);
        RecordDataCollectionCommandFactory.TryCreate(request, "NV1", "operator", out var original, out _).ShouldBeTrue();
        foreach (var mutation in new[]
        {
            request with { OccurredAt = request.OccurredAt.AddSeconds(1) },
            request with { Payload = request.Payload with { Serial = "NV1PP16250A00002" } },
            request with { Payload = request.Payload with { OperationRunId = "other" } },
            request with { Payload = request.Payload with { StepCode = "other" } },
            request with { Payload = request.Payload with { EquipmentPath = "other" } },
            request with { Payload = request.Payload with { SignalCode = "other" } },
            request with { Payload = request.Payload with { Value = 401.26m } },
            request with { Payload = request.Payload with { UnitOfMeasure = "other" } },
        })
        {
            RecordDataCollectionCommandFactory.TryCreate(mutation, "NV1", "operator", out var changed, out _).ShouldBeTrue();
            changed!.CanonicalPayload.ShouldNotBe(original!.CanonicalPayload);
            changed.IdempotencyKey.ShouldBe(original.IdempotencyKey);
        }
    }

    [Fact]
    public void MapperUsesAuthenticatedActorAndRefusesClaimedForeignSite()
    {
        var request = Request(401.25m);
        RecordDataCollectionCommandFactory.TryCreate(request, "NV1", "authenticated-subject", out var command, out _).ShouldBeTrue();
        command!.ActorId.ShouldBe("authenticated-subject");
        command.SiteId.ShouldBe("NV1");
        RecordDataCollectionCommandFactory.TryCreate(request, "DE1", "authenticated-subject", out _, out var error).ShouldBeFalse();
        error.ShouldBe(RecordDataCollectionMappingError.SiteMismatch);
    }

    private static RecordDataCollectionRequest Request(decimal value)
    {
        const string submission = "53cae326-bc29-4451-82d1-68dfcbf5913b";
        return new(RecordDataCollectionCommand.KeyFor("NV1", submission).Value.ToString("D"), "NV1",
            DateTimeOffset.Parse("2026-09-15T03:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new(submission, "NV1PP16250A00001", "OPRUN-NV1-EOL-0001", "EOL", "NOVAVOLT/NV1/PACK/P1/EOL-01", "PackVoltage", value, "V"));
    }
}
