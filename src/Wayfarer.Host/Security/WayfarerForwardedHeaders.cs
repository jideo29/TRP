using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Security;

/// <summary>
/// X7: the client address used by the anonymous rate-limit bucket comes from
/// <c>X-Forwarded-For</c> only when the connection is from the gateway. The framework's loopback default is
/// removed. <c>ForwardedHeadersMiddleware</c> skips its known-proxy check when both lists are empty, so
/// <see cref="UseWayfarerForwardedHeaders"/> adds the middleware only when <see cref="TrustsForwardedHeaders"/> is
/// true; with nothing configured no forwarded header is read and every caller keeps its own connection address.
/// Only the closest hop is read (<c>ForwardLimit = 1</c>). A malformed entry refuses start.
/// </summary>
public static class WayfarerForwardedHeaders
{
    public const string KnownProxiesSetting = "ForwardedHeaders:KnownProxies";
    public const string KnownNetworksSetting = "ForwardedHeaders:KnownNetworks";

    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var value in ReadList(configuration, KnownProxiesSetting))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException($"{KnownProxiesSetting} entry '{value}' is not an IP address.");
            options.KnownProxies.Add(address);
        }

        foreach (var value in ReadList(configuration, KnownNetworksSetting))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException($"{KnownNetworksSetting} entry '{value}' is not a CIDR network.");
            options.KnownIPNetworks.Add(network);
        }
    }

    /// <summary>True only when at least one gateway address or network is configured.</summary>
    public static bool TrustsForwardedHeaders(IConfiguration configuration) =>
        ReadList(configuration, KnownProxiesSetting).Any() || ReadList(configuration, KnownNetworksSetting).Any();

    /// <summary>Adds the forwarded-headers middleware only when a gateway is configured.</summary>
    public static WebApplication UseWayfarerForwardedHeaders(this WebApplication app)
    {
        if (TrustsForwardedHeaders(app.Configuration))
            app.UseForwardedHeaders();
        return app;
    }

    private static IEnumerable<string> ReadList(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var children = section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (children.Count > 0)
            return children;

        return (section.Value ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
