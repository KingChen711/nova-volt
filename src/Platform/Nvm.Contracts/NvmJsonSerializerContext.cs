using System.Text.Json.Serialization;
using Nvm.Contracts.CloudEvents;
using Nvm.Contracts.Events.FactoryModel;

namespace Nvm.Contracts;

/// <summary>
/// The single serializer configuration for everything that travels between services or lands in the
/// event store.
/// </summary>
/// <remarks>
/// <para>
/// Source-generated rather than reflection-based. Two reasons that matter here, in order:
/// </para>
/// <list type="number">
///   <item><description>
///     A type that cannot be serialized becomes a <b>build</b> error instead of an exception thrown
///     at three in the morning on the one code path nobody exercised.
///   </description></item>
///   <item><description>
///     Every envelope shape has to be declared below by hand. That is not friction to route around —
///     it is a checkpoint. Adding a line here is the moment to ask whether the event needs a golden
///     file, and the answer is always yes.
///   </description></item>
/// </list>
/// <para>
/// <c>PropertyNamingPolicy</c> is camelCase, which is the convention for the fields inside
/// <c>data</c>. The CloudEvents attributes around it are lower case with no separators and are named
/// one by one on the envelope, because a naming policy would render <c>specversion</c> as
/// <c>specVersion</c> and quietly emit something that is no longer CloudEvents.
/// </para>
/// <para>
/// <c>WhenWritingNull</c> keeps unset optional attributes out of the document entirely, which is what
/// the specification asks for: an absent attribute means absent, while <c>"subject": null</c> is a
/// statement that the subject is known to be nothing.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CloudEventEnvelope<FactoryModelRevisionActivated>))]
public sealed partial class NvmJsonSerializerContext : JsonSerializerContext;
