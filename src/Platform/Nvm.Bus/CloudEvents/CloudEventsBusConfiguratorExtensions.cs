using System.Reflection;
using MassTransit;
using Nvm.Contracts.Events;

namespace Nvm.Bus.CloudEvents;

/// <summary>Cài đặt filter đóng dấu CloudEvents cho mọi event đã khai báo.</summary>
/// <remarks>
/// Viết dựa trên <see cref="IBusFactoryConfigurator"/> thay vì cái dành riêng cho RabbitMQ là có chủ ý:
/// việc đóng dấu là về message, không phải về transport mang nó đi. Điều đó cũng có nghĩa là một test có
/// thể chạy filter thật trên transport in-memory thay vì phải cài lại logic của nó — một test tự đóng
/// dấu header của riêng mình thì vẫn pass ngay cả khi filter đã bị xóa.
/// </remarks>
public static class CloudEventsBusConfiguratorExtensions
{
    /// <summary>Đóng dấu mọi event gửi đi đã khai báo với các thuộc tính CloudEvents của nó.</summary>
    /// <param name="configurator">Bus đang được cấu hình.</param>
    /// <param name="applicationName">Tiến trình này là deployable nào, viết dạng kebab-case.</param>
    public static void UseNvmCloudEvents(this IBusFactoryConfigurator configurator, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var install = typeof(CloudEventsBusConfiguratorExtensions)
            .GetMethod(nameof(InstallFilterFor), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var eventType in DeclaredEventTypes.All())
        {
            install.MakeGenericMethod(eventType).Invoke(null, [configurator, applicationName]);
        }
    }

    private static void InstallFilterFor<TEvent>(IBusFactoryConfigurator configurator, string applicationName)
        where TEvent : class, IDomainEvent
    {
        var filter = new CloudEventsSendFilter<TEvent>(applicationName);

        // Cả hai pipe. Publish và send là hai đường tách biệt trong MassTransit; mọi thứ ở đây đi ra qua
        // Publish, và send cũng được bao phủ để một lượt send trực tiếp tới endpoint không bị bỏ sót.
        // Cần cast vì một class triển khai cả hai filter interface; không có cast thì compiler sẽ chọn
        // overload không generic và mất luôn message type.
        configurator.ConfigurePublish(pipe => pipe.UseFilter((IFilter<PublishContext<TEvent>>)filter));
        configurator.ConfigureSend(pipe => pipe.UseFilter((IFilter<SendContext<TEvent>>)filter));
    }
}
