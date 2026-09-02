using System.Diagnostics.CodeAnalysis;
using Nvm.Kernel.Identity;

namespace Nvm.Sparkplug.Topics;

/// <summary>Một topic MQTT trong namespace Sparkplug B, và vị trí trong nhà máy mà nó định danh.</summary>
/// <remarks>
/// <para>
/// Một payload Sparkplug không mang địa chỉ nào. Máy nào đã gửi nó, và message này khai báo hay cập
/// nhật, nằm trọn trong topic — nên type này là điểm nối giữa thế giới device và cây ISA-95 mà M1 đã
/// xây. Ánh xạ này được cố định bởi docs/scope.md §7.1:
/// </para>
/// <code>
/// spBv1.0/NOVAVOLT-NV1-FORMATION/DDATA/EDGE-F1/FORM-01-CH-0142
///         └ enterprise, site, area ┘      └line┘ └── device ──┘
///
/// NOVAVOLT/NV1/FORMATION/F1/FORM-01/FORM-01-CH-0142
///                           └ work cell, from the model ┘
/// </code>
/// <para>
/// <b>Chữ hoa, và không phải vì sở thích.</b> Cùng những máy đó còn được định danh bởi cây Unified
/// Namespace, vốn là chữ thường: <c>novavolt/nv1/formation/f1/form-01/ch-0142</c>. Chấp nhận cả hai
/// cách viết ở đây sẽ khiến một cycler có hai identity, và mọi phép đếm downstream sẽ bị chiếm mất
/// một nửa dữ liệu. <see cref="EquipmentPath"/> đã từ chối chữ thường sẵn; việc parse đi qua nó để
/// type này không cần đưa ra một ý kiến thứ hai.
/// </para>
/// <para>
/// <b>Chiều ngược lại làm mất work cell.</b> Một topic có bốn cấp nhà máy còn một path có tới sáu, nên
/// <see cref="For"/> bỏ work cell đi và chỉ có <see cref="ResolveEquipmentPath"/> mới đưa nó trở lại
/// được — bằng cách hỏi model. Sự bất đối xứng đó là lý do directory tồn tại, và test round-trip đi
/// qua cả hai chiều thay vì chỉ qua <see cref="For"/>.
/// </para>
/// </remarks>
public sealed record SparkplugTopic
{
    /// <summary>Namespace Sparkplug B, cấp đầu tiên của mọi topic.</summary>
    public const string Namespace = "spBv1.0";

    /// <summary>Tiền tố mà một edge node id mang trước line code.</summary>
    public const string EdgeNodePrefix = "EDGE-";

    private const char LevelSeparator = '/';
    private const char GroupSeparator = '-';
    private const int NodeLevelCount = 4;
    private const int DeviceLevelCount = 5;

    private SparkplugTopic(
        string value,
        SparkplugMessageType messageType,
        EquipmentPath linePath,
        string? deviceCode)
    {
        Value = value;
        MessageType = messageType;
        LinePath = linePath;
        DeviceCode = deviceCode;
    }

    /// <summary>Topic đúng như nó đi trên MQTT.</summary>
    public string Value { get; }

    /// <summary>Message này dùng để làm gì.</summary>
    public SparkplugMessageType MessageType { get; }

    /// <summary>Line mà edge node đại diện, dưới dạng một ISA-95 path.</summary>
    /// <remarks>
    /// Luôn bốn segment, và luôn là những gì topic <i>khai báo</i>. Nhà máy có thực sự có line đó hay
    /// không là câu hỏi của <see cref="ResolveEquipmentPath"/>, không phải của property này.
    /// </remarks>
    public EquipmentPath LinePath { get; }

    /// <summary>Device code, hoặc null với một message ở mức node.</summary>
    public string? DeviceCode { get; }

    /// <summary>Enterprise code.</summary>
    public string EnterpriseCode => LinePath.Segments[0];

    /// <summary>Plant. First class ở mọi nơi, theo K3.</summary>
    public string SiteId => LinePath.Segments[1];

    /// <summary>Area code.</summary>
    public string AreaCode => LinePath.Segments[2];

    /// <summary>Line code.</summary>
    public string LineCode => LinePath.Segments[3];

    /// <summary>Sparkplug group id, <c>{enterprise}-{site}-{area}</c>.</summary>
    public string GroupId => string.Join(GroupSeparator, EnterpriseCode, SiteId, AreaCode);

    /// <summary>Sparkplug edge node id, <c>EDGE-{line}</c>.</summary>
    public string EdgeNodeId => EdgeNodePrefix + LineCode;

    /// <summary>Parse một topic, throw khi nó không phải một topic hợp lệ.</summary>
    /// <param name="value">Topic MQTT.</param>
    /// <exception cref="FormatException">Topic không phải một topic Sparkplug B mà hệ thống này đọc được.</exception>
    public static SparkplugTopic Parse(string? value) =>
        TryParse(value, out var topic)
            ? topic
            : throw new FormatException($"Not a Sparkplug B topic this system can read: '{value}'.");

    /// <summary>Parse một topic, trả về false khi nó không phải một topic hợp lệ.</summary>
    /// <param name="value">Topic MQTT.</param>
    /// <param name="topic">Topic đã parse.</param>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out SparkplugTopic? topic)
    {
        topic = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var levels = value.Split(LevelSeparator);

        if (levels.Length is not (NodeLevelCount or DeviceLevelCount))
        {
            return false;
        }

        if (!string.Equals(levels[0], Namespace, StringComparison.Ordinal))
        {
            return false;
        }

        if (!SparkplugMessageTypes.TryParse(levels[2], out var messageType))
        {
            return false;
        }

        // Một DDATA không có device, hoặc một NDATA có device, là một publisher không đồng thuận với
        // spec về việc mình đang gửi gì. Vẫn đọc nó nghĩa là phải đoán nửa nào đúng, trên một message
        // mà hai nửa nói hai điều khác nhau về cùng một máy.
        var deviceCode = levels.Length == DeviceLevelCount ? levels[DeviceLevelCount - 1] : null;

        if (messageType.IsDeviceLevel() != (deviceCode is not null))
        {
            return false;
        }

        // Phần dư dồn vào area, nên một area code có thể chứa dấu gạch ngang. Enterprise và site thì
        // không được — For() từ chối build một topic từ các code không sống sót qua phép split này,
        // đó là nơi sự mập mờ bị bắt lại, chứ không phải ở đây, nơi nó không thể phát hiện được.
        var group = levels[1].Split(GroupSeparator, 3);

        if (group.Length != 3 || !levels[3].StartsWith(EdgeNodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var lineCode = levels[3][EdgeNodePrefix.Length..];

        // Đi qua EquipmentPath, nơi chữ hoa, hình dạng segment và độ sâu đã được quyết định sẵn. Một
        // bộ quy tắc thứ hai ở đây sẽ là một câu trả lời thứ hai cho cùng một câu hỏi.
        if (!EquipmentPath.TryParse(string.Join(EquipmentPath.Separator, group[0], group[1], group[2], lineCode), out var linePath)
            || linePath.Kind != FactoryNodeKind.Line)
        {
            return false;
        }

        if (deviceCode is not null
            && !EquipmentPath.TryParse($"{linePath.Value}{EquipmentPath.Separator}{deviceCode}", out _))
        {
            return false;
        }

        topic = new SparkplugTopic(value, messageType, linePath, deviceCode);
        return true;
    }

    /// <summary>Một topic có phải topic host state của Sparkplug hay không, <c>spBv1.0/STATE/{host}</c>.</summary>
    /// <param name="value">Topic MQTT.</param>
    /// <remarks>
    /// Một hình dạng hoàn toàn khác: nó định danh một SCADA host, không phải một vị trí trong nhà máy,
    /// nên <see cref="TryParse"/> từ chối nó. Gateway subscribe <c>spBv1.0/#</c> và sẽ nhận được nó,
    /// và cần phân biệt "không gửi cho mình" với "lỗi định dạng" — một cái là bình thường, cái kia
    /// đáng để cảnh báo.
    /// </remarks>
    public static bool IsHostState(string? value) =>
        value is not null
        && value.StartsWith($"{Namespace}{LevelSeparator}STATE{LevelSeparator}", StringComparison.Ordinal);

    /// <summary>Build topic mà một vị trí trong nhà máy publish lên.</summary>
    /// <param name="path">Một line cho message ở mức node, hoặc một work cell/equipment cho message ở mức device.</param>
    /// <param name="messageType">Message này dùng để làm gì.</param>
    /// <exception cref="ArgumentException">
    /// Path ở sai mức so với message type, hoặc enterprise/site code của nó chứa dấu gạch ngang nên
    /// không thể đọc lại được từ group id.
    /// </exception>
    /// <remarks>
    /// Cần cho simulator ở C05, vốn publish thay mặt cho nhà máy. Đây cũng là nửa của round-trip có
    /// thể kiểm tra được mà không cần factory model.
    /// </remarks>
    public static SparkplugTopic For(EquipmentPath path, SparkplugMessageType messageType)
    {
        ArgumentNullException.ThrowIfNull(path);

        var deviceLevel = messageType.IsDeviceLevel();

        var expected = deviceLevel
            ? path.Kind is FactoryNodeKind.WorkCell or FactoryNodeKind.Equipment
            : path.Kind is FactoryNodeKind.Line;

        if (!expected)
        {
            throw new ArgumentException(
                $"{messageType.Token()} is {(deviceLevel ? "device" : "node")}-level, so it cannot be "
                + $"published for '{path.Value}', which is a {path.Kind}.",
                nameof(path));
        }

        var enterprise = path.Segments[0];
        var site = path.Segments[1];
        var area = path.Segments[2];
        var line = path.Segments[3];

        // Group id nối ba code bằng đúng ký tự mà bản thân một code có thể chứa, nên phép split ngược
        // lại chỉ hết mập mờ nếu hai code đầu sạch. Từ chối ngay tại điểm build, vì tại điểm parse thì
        // chuyện này vô hình — topic sẽ chỉ resolve về một line mà nhà máy không có, đọc lên giống như
        // một lỗi cấu hình ở đâu đó khác.
        if (enterprise.Contains(GroupSeparator, StringComparison.Ordinal)
            || site.Contains(GroupSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Enterprise '{enterprise}' and site '{site}' must not contain '{GroupSeparator}': the "
                + "Sparkplug group id joins enterprise, site and area with it, and the area takes the "
                + "remainder when it is split back.",
                nameof(path));
        }

        var groupId = string.Join(GroupSeparator, enterprise, site, area);
        var value = deviceLevel
            ? string.Join(LevelSeparator, Namespace, groupId, messageType.Token(), EdgeNodePrefix + line, path.Code)
            : string.Join(LevelSeparator, Namespace, groupId, messageType.Token(), EdgeNodePrefix + line);

        var linePath = EquipmentPath.Parse(string.Join(EquipmentPath.Separator, enterprise, site, area, line));

        return new SparkplugTopic(value, messageType, linePath, deviceLevel ? path.Code : null);
    }

    /// <summary>Tìm vị trí trong nhà máy mà topic này trỏ tới, hoặc null khi nhà máy không có vị trí đó.</summary>
    /// <param name="directory">Model mà mỗi nhà máy đang chạy hiện tại.</param>
    /// <returns>
    /// Line cho message ở mức node, work cell hoặc equipment cho message ở mức device, hoặc null.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Null thay vì exception, vì đây là nơi K3 được thực thi và câu trả lời là một quyết định routing,
    /// không phải một lỗi: một topic trích dẫn một nhà máy chưa ai kích hoạt, một line đã ngừng hoạt
    /// động, hoặc một device thuộc một revision mà nhà máy này chưa được rollout tới đều rơi vào đây.
    /// Ingestion từ chối message và nói rõ đã từ chối topic nào — nó không dừng lại.
    /// </para>
    /// <para>
    /// Đây cũng là cách duy nhất để quay lại một path sáu segment. Topic biết device code nhưng không
    /// biết work cell nào đang chứa nó, nên chỉ có model mới nói được <c>FORM-01-CH-0142</c> treo dưới
    /// line hay dưới một cycler.
    /// </para>
    /// </remarks>
    public EquipmentPath? ResolveEquipmentPath(IEquipmentDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (DeviceCode is null)
        {
            return directory.Contains(LinePath) ? LinePath : null;
        }

        return directory.FindDevice(LinePath, DeviceCode);
    }

    /// <summary>Trả về topic.</summary>
    public override string ToString() => Value;

    /// <summary>So sánh hai topic theo văn bản của chúng.</summary>
    /// <remarks>
    /// Equality tự sinh của record sẽ so sánh <see cref="LinePath"/> theo reference và coi hai topic
    /// giống hệt nhau là khác nhau. Ordinal, cùng lý do như các path: đây là địa chỉ máy, và một phép
    /// so sánh theo culture có thể quyết định hai máy khác nhau lại trùng nhau.
    /// </remarks>
    public bool Equals(SparkplugTopic? other) =>
        other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}
