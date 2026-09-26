using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nvm.Contracts.Queries;

namespace Nvm.Quality.Hosting;

public static class QualityRegistration
{
    /// <summary>Đăng ký sau AddNvmCommandStore và event store; facet dùng chung session của command.</summary>
    public static IServiceCollection AddNvmQuality(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IUnitQualityFacet, SqlUnitQualityFacet>();
        return services;
    }
}
