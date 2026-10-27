using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wayfarer.Security;

/// <summary>
/// X7 (ASVS 11.1.4): a global limiter partitioned by authenticated client and tenant (client address when
/// anonymous). The host places it after authentication, so the partition key comes from the validated
/// principal. A rejected call answers 429 with <c>Retry-After</c> and a problem body. The limiter cannot be
/// switched off outside Development. The anonymous address comes from <see cref="WayfarerForwardedHeaders"/>.
/// </summary>
public static class WayfarerRateLimiting
{
    public const string EnabledSetting = "RateLimiting:Enabled";
    public const string RateLimitedCode = "wayfarer.rate_limited";

    public static IServiceCollection AddWayfarerRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (IsDisabled(configuration) && !environment.IsDevelopment())
            throw new InvalidOperationException($"{EnabledSetting}=false is refused outside Development.");

        // Values are read when the options are built, so configuration added by the host builder is included.
        services.AddRateLimiter(options =>
        {
            var window = TimeSpan.FromSeconds(ReadInt(configuration, "RateLimiting:WindowSeconds", 60));
            var permit = ReadInt(configuration, "RateLimiting:PermitLimit", 600);
            var anonymousPermit = ReadInt(configuration, "RateLimiting:AnonymousPermitLimit", 60);
            var disabled = IsDisabled(configuration);
            if (disabled && !environment.IsDevelopment())
                throw new InvalidOperationException($"{EnabledSetting}=false is refused outside Development.");

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retry = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : window;
                var response = context.HttpContext.Response;
                response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                response.ContentType = "application/problem+json";
                await response.WriteAsJsonAsync(
                    new { type = "about:blank", title = "Too many requests", status = 429, code = RateLimitedCode },
                    cancellationToken).ConfigureAwait(false);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                if (disabled || http.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                    return RateLimitPartition.GetNoLimiter("unlimited");

                var key = PartitionKey(http);
                var limit = key.StartsWith("anonymous:", StringComparison.Ordinal) ? anonymousPermit : permit;
                return RateLimitPartition.GetFixedWindowLimiter(
                    key,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = window, QueueLimit = 0 });
            });
        });

        return services;
    }

    /// <summary>Client and tenant for an authenticated caller; the client address otherwise.</summary>
    public static string PartitionKey(HttpContext http)
    {
        var user = http.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var client = user.FindFirst("azp")?.Value
                ?? user.FindFirst("client_id")?.Value
                ?? user.FindFirst("sub")?.Value
                ?? "unknown";
            var tenant = user.FindFirst(WayfarerAuthentication.TenantClaim)?.Value
                ?? user.FindFirst(WayfarerAuthentication.LegacyTenantClaim)?.Value
                ?? "none";
            return "client:" + client + "|tenant:" + tenant;
        }

        return "anonymous:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static bool IsDisabled(IConfiguration configuration) =>
        string.Equals(configuration[EnabledSetting]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
}
