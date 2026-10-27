using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Nvm.Foundation.Web;

/// <summary>Cấu hình service layer HTTP của một plant.</summary>
public sealed class MomWebOptions
{
    public string Plant { get; set; } = "NV1";

    /// <summary>URL realm Keycloak (nhà cung cấp danh tính). Null khi test ký token bằng <see cref="SigningKey"/>.</summary>
    public string? Authority { get; set; }

    public string? Audience { get; set; }

    public string? Issuer { get; set; }

    /// <summary>Khóa ký token cho test; host thật lấy khóa từ <see cref="Authority"/>.</summary>
    public SecurityKey? SigningKey { get; set; }
}

/// <summary>Xác thực bằng JWT: token có hạn (realm đặt khoảng 10 phút); hết hạn thì 401 mã <c>6401</c>, như nền thật.</summary>
public static class MomAuthentication
{
    public const string ExpiredTokenCode = "6401";

    public static IServiceCollection AddMomAuthentication(this IServiceCollection services, MomWebOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddAuthorization();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            // Giữ tên claim gốc của token (preferred_username, realm_access), không đổi sang tên dài kiểu WS-Fed.
            jwt.MapInboundClaims = false;
            jwt.RequireHttpsMetadata = false;
            jwt.Authority = options.Authority;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = options.Issuer is not null || options.Authority is not null,
                ValidIssuer = options.Issuer,
                ValidateAudience = options.Audience is not null,
                ValidAudience = options.Audience,
                IssuerSigningKey = options.SigningKey,
                ValidateIssuerSigningKey = true,
                // Không cộng 5 phút dung sai mặc định: hết hạn là hết hạn, để tái hiện được lỗi 6401 đúng lúc.
                ClockSkew = TimeSpan.Zero,
                NameClaimType = "preferred_username",
            };
            jwt.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    var expired = context.AuthenticateFailure is SecurityTokenExpiredException;
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = new
                        {
                            code = expired ? ExpiredTokenCode : "Unauthorized",
                            message = expired ? "The access token has expired. Request a new token." : "A valid access token is required.",
                        },
                    }).ConfigureAwait(false);
                },
            };
        });
        return services;
    }

    /// <summary>Người gọi từ token: tên đăng nhập và realm role.</summary>
    public static CallerIdentity Caller(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in user.FindAll("roles"))
        {
            roles.Add(claim.Value);
        }

        if (user.FindFirst("realm_access")?.Value is { } realmAccess)
        {
            using var document = JsonDocument.Parse(realmAccess);
            if (document.RootElement.TryGetProperty("roles", out var list))
            {
                foreach (var role in list.EnumerateArray())
                {
                    roles.Add(role.GetString()!);
                }
            }
        }

        return new CallerIdentity(user.FindFirst("preferred_username")?.Value ?? user.FindFirst("sub")?.Value ?? "unknown", roles);
    }
}
