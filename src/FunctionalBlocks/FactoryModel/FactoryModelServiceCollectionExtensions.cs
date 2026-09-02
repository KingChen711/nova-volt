using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.FactoryModel.Seeding;
using Nvm.FactoryModel.Storage;
using Nvm.FactoryModel.Time;
using Nvm.Kernel.Identity;
using Nvm.Time;

namespace Nvm.FactoryModel;

/// <summary>Gắn Functional Block FactoryModel vào một host.</summary>
public static class FactoryModelServiceCollectionExtensions
{
    /// <summary>Load mọi revision của model và đăng ký mọi thứ block này cần.</summary>
    /// <param name="services">Container đang được build.</param>
    /// <param name="seedDirectoryPath">Directory chứa các document <c>factory-model.r*.json</c>.</param>
    /// <exception cref="DirectoryNotFoundException">Seed directory không tồn tại.</exception>
    /// <exception cref="FactoryModelSeedException">Một document không mô tả một plant hợp lệ.</exception>
    /// <remarks>
    /// <para>
    /// Các document được đọc <b>ngay tại đây</b>, trong lúc container đang được build, để một file
    /// hỏng dừng process lại trước khi nó kịp phục vụ bất cứ điều gì. Trì hoãn việc đọc tới lần dùng
    /// đầu tiên sẽ để một service khởi động khoẻ mạnh, chấp nhận traffic, rồi mới thất bại ở đúng
    /// request nào tình cờ cần đến model trước — biến một lỗi cấu hình thành một sự cố runtime chập
    /// chờn.
    /// </para>
    /// <para>
    /// <b>Cả directory, không phải một file.</b> Mọi revision đều được load, vì revision mà một plant
    /// sắp activate và revision nó đang chạy thường là hai document khác nhau, và event thông báo việc
    /// chuyển đổi phải diff cả hai. Chỉ load bản mới nhất sẽ khiến phép diff đó không thể tính được và
    /// staged rollout không thể diễn đạt được.
    /// </para>
    /// <para>
    /// Command handler và validator đến từ assembly scan trong <c>AddNvmKernel</c>, mà host gọi với
    /// assembly này. Phương thức này chỉ đăng ký những gì mà scan không tự tìm ra được.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNvmFactoryModel(this IServiceCollection services, string seedDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IFactoryModelCatalog>(FactoryModelSeed.LoadCatalog(seedDirectoryPath));
        services.TryAddSingleton<IActiveFactoryModel, InMemoryActiveFactoryModel>();

        // Câu trả lời của block này cho một câu hỏi thuộc Platform (Nvm.Kernel.Identity.IEquipmentDirectory).
        // Ingestion và edge gateway resolve mã máy thông qua interface này và không bao giờ nhìn thấy
        // type cụ thể này, đó là điều giữ K8 nguyên vẹn trong khi vẫn cho phép chúng đặt câu hỏi.
        services.TryAddSingleton<IEquipmentDirectory, FactoryModelEquipmentDirectory>();

        return services;
    }

    /// <summary>Đăng ký production calendar, đọc time zone của từng plant từ model.</summary>
    /// <param name="services">Container đang được build.</param>
    /// <remarks>
    /// <para>
    /// Tách riêng khỏi <see cref="AddNvmFactoryModel"/> và là opt-in, vì nó cần một thứ mà bản thân
    /// block không cần: một <see cref="TimeProvider"/> đã được đăng ký. Một host chưa quyết định đồng
    /// hồ của mình là gì thì chưa xứng đáng có một calendar (K1).
    /// </para>
    /// <para>
    /// Đây là nơi duy nhất <see cref="ISiteCalendarDirectory"/> nên được đăng ký trong một host có
    /// factory model. Một <c>InMemorySiteCalendarDirectory</c> nằm bên cạnh nó sẽ chính là bảng tra
    /// cứu thứ hai mà thiết kế này tồn tại để tránh.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddNvmProductionCalendar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISiteCalendarDirectory, FactoryModelSiteCalendarDirectory>();
        services.TryAddSingleton<IProductionCalendar, ProductionCalendar>();

        return services;
    }
}
