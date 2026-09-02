using System.Reflection;
using MassTransit;

namespace Nvm.Bus.Topology;

/// <summary>Đặt tên queue theo <i>công dụng</i> của consumer, không bao giờ theo tên class của nó.</summary>
/// <remarks>
/// Xem <see cref="BusEndpointAttribute"/> để biết lý do. Một consumer chưa khai báo endpoint của nó sẽ
/// bị từ chối ngay tại đây, lúc khởi động, với một thông báo nêu tên class — thay vì được cấp một tên
/// tự sinh mà âm thầm trở thành một queue thứ hai ở lần đổi tên kế tiếp.
/// </remarks>
public sealed class NvmEndpointNameFormatter : IEndpointNameFormatter
{
    /// <summary>Instance duy nhất; formatter không giữ state nào.</summary>
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
