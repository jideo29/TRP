using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Wayfarer.Security;

/// <summary>
/// Startup checks. The host does not start when one fails.
/// <list type="bullet">
/// <item>X1: <c>Deployment:Mode=Standalone</c> (local sessions, stand-ins) is Development-only.</item>
/// <item>M52 / X2 / X5: outside Development, Aegis issuer, JWKS and introspection URLs are https, an audience is
/// set, and the <c>wayfarer-introspection</c> client secret is present.</item>
/// <item>X3 / ASVS 1.9.1, 9.2.2: https-only listeners (no http URLs, empty HTTP_PORTS, at least one https
/// listener); Pulse, Atlas, Sentinel, Titan and the Aegis token URL are https when set.</item>
/// <item>L20 / ASVS 1.2.2: a configured Titan URL needs the Wayfarer client credential (<c>Titan:ClientId</c>,
/// <c>Titan:ClientSecret</c>, <c>Aegis:TokenUrl</c>).</item>
/// <item>X7: the rate limiter cannot be switched off.</item>
/// </list>
/// </summary>
public static class WayfarerSecurityBaseline
{
    public static readonly IReadOnlyList<string> PeerUrlSettings =
    [
        "Pulse:BaseUrl", "Atlas:BaseUrl", "Sentinel:BaseUrl",
        TitanClientCredentials.CustomerDirectoryUrlSetting, TitanClientCredentials.LedgerUrlSetting, "Aegis:TokenUrl",
    ];

    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration["Deployment:Mode"];
        if (TitanCustomerLedgerBind.IsStandalone(mode) && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Deployment:Mode=Standalone is Development-only. Environment '{environment.EnvironmentName}' refuses to start; "
                + "configure Aegis and leave Deployment:Mode unset.");
        }

        if (environment.IsDevelopment())
            return;

        var aegis = new WayfarerAegisOptions();
        configuration.GetSection(WayfarerAegisOptions.Section).Bind(aegis);
        RequireHttps("Aegis:Issuer", aegis.Issuer);
        RequireHttps("Aegis:JwksUrl", aegis.JwksUrl);
        RequireHttps("Aegis:IntrospectionUrl", aegis.IntrospectionUrl);
        if (string.IsNullOrWhiteSpace(aegis.Audience))
            throw new InvalidOperationException("Aegis:Audience is required outside Development.");
        if (string.IsNullOrWhiteSpace(aegis.IntrospectionClientId) || string.IsNullOrWhiteSpace(aegis.IntrospectionClientSecret))
        {
            throw new InvalidOperationException(
                "Aegis:IntrospectionClientId and Aegis:IntrospectionClientSecret are required outside Development "
                + $"(the {WayfarerAegisOptions.DefaultIntrospectionClientId} client).");
        }

        // Aegis contract: Wayfarer introspects only as its own introspection client.
        if (!string.Equals(aegis.IntrospectionClientId.Trim(), WayfarerAegisOptions.DefaultIntrospectionClientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Aegis:IntrospectionClientId must be {WayfarerAegisOptions.DefaultIntrospectionClientId} outside Development.");
        }

        ValidateListeners(configuration);

        foreach (var key in PeerUrlSettings)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value) && !WayfarerAegisOptions.IsHttps(value))
                throw new InvalidOperationException($"{key} must be an https URL outside Development.");
        }

        if (TitanClientCredentials.TitanConfigured(configuration) && !TitanClientCredentials.CredentialsConfigured(configuration))
        {
            throw new InvalidOperationException(
                "A Titan URL is set without the Wayfarer client credential (Titan:ClientId, Titan:ClientSecret, Aegis:TokenUrl).");
        }

        if (string.Equals(configuration[WayfarerRateLimiting.EnabledSetting]?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{WayfarerRateLimiting.EnabledSetting}=false is refused outside Development.");
    }

    private static void RequireHttps(string key, string? value)
    {
        if (!WayfarerAegisOptions.IsHttps(value))
            throw new InvalidOperationException($"{key} must be an https URL outside Development.");
    }

    internal static void ValidateListeners(IConfiguration configuration)
    {
        if (CountHttpsListeners(configuration) == 0)
        {
            throw new InvalidOperationException(
                "No https listener is configured. Set urls / ASPNETCORE_URLS to an https:// address, HTTPS_PORTS, " +
                "or a Kestrel https endpoint. Without one Kestrel falls back to plaintext http.");
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url) && url.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Kestrel:Endpoints:{endpoint.Key}:Url must be https outside Development.");
        }

        var urls = Urls(configuration);
        if (!string.IsNullOrWhiteSpace(urls)
            && urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Listener URLs must be https outside Development. Remove http:// entries.");
        }

        var httpPorts = configuration["http_ports"]
            ?? configuration["HTTP_PORTS"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")
            ?? Environment.GetEnvironmentVariable("HTTP_PORTS");
        if (!string.IsNullOrWhiteSpace(httpPorts))
        {
            throw new InvalidOperationException(
                "HTTP_PORTS / ASPNETCORE_HTTP_PORTS must be empty outside Development. Serve https only.");
        }
    }

    internal static int CountHttpsListeners(IConfiguration configuration)
    {
        var count = 0;
        var urls = Urls(configuration);
        if (!string.IsNullOrWhiteSpace(urls))
        {
            count += urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        var httpsPorts = configuration["https_ports"]
            ?? configuration["HTTPS_PORTS"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS")
            ?? Environment.GetEnvironmentVariable("HTTPS_PORTS");
        if (!string.IsNullOrWhiteSpace(httpsPorts))
            count += httpsPorts.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;

        count += configuration.GetSection("Kestrel:Endpoints").GetChildren()
            .Count(e => e["Url"]?.Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true);
        return count;
    }

    private static string? Urls(IConfiguration configuration) =>
        configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
}
