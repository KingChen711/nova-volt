using System.Reflection;
using MassTransit;

namespace Nvm.Bus.Topology;

/// <summary>Names queues from what a consumer <i>is for</i>, never from what its class is called.</summary>
/// <remarks>
/// See <see cref="BusEndpointAttribute"/> for why. A consumer that has not declared its endpoint is
/// refused here, at startup, with a message naming the class — rather than being handed a generated
/// name that silently becomes a second queue on the next rename.
/// </remarks>
public sealed class NvmEndpointNameFormatter : IEndpointNameFormatter
{
    /// <summary>The single instance; the formatter has no state.</summary>
    public static readonly NvmEndpointNameFormatter Instance = new();

    private const string TemporaryPrefix = "tmp";

    private NvmEndpointNameFormatter()
    {
    }

    /// <inheritdoc />
    public string Separator => ".";

    /// <inheritdoc />
    public string TemporaryEndpoint(string tag) =>
        string.Join(Separator, NvmTopology.Prefix, TemporaryPrefix, SanitizeName(tag ?? "endpoint"));

    /// <inheritdoc />
    public string Consumer<T>()
        where T : class, IConsumer =>
        NameOf(typeof(T));

    /// <inheritdoc />
    public string Message<T>()
        where T : class =>
        NameOf(typeof(T));

    /// <inheritdoc />
    public string Saga<T>()
        where T : class, ISaga =>
        throw new NotSupportedException(
            $"Sagas are not part of this milestone; '{typeof(T).Name}' cannot be named yet. Sagas arrive with formation and aging.");

    /// <inheritdoc />
    public string ExecuteActivity<T, TArguments>()
        where T : class, IExecuteActivity<TArguments>
        where TArguments : class =>
        throw new NotSupportedException($"Courier activities are not used in this system; '{typeof(T).Name}'.");

    /// <inheritdoc />
    public string CompensateActivity<T, TLog>()
        where T : class, ICompensateActivity<TLog>
        where TLog : class =>
        throw new NotSupportedException($"Courier activities are not used in this system; '{typeof(T).Name}'.");

    /// <inheritdoc />
    public string SanitizeName(string name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToLowerInvariant();

    private string NameOf(Type consumerType)
    {
        var endpoint = consumerType.GetCustomAttribute<BusEndpointAttribute>()
            ?? throw new InvalidOperationException(
                $"Consumer '{consumerType.Name}' has no [BusEndpoint]. A queue name is an operational "
                + "contract and must be stated, not derived from the class name.");

        return string.Join(
            Separator,
            NvmTopology.Prefix,
            SanitizeName(endpoint.Context),
            SanitizeName(endpoint.Role));
    }
}
