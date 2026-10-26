namespace Wayfarer.Security;

/// <summary>
/// Inbound Aegis settings (section <c>Aegis</c>). Outside Development the host refuses to start unless the issuer,
/// JWKS, introspection URL and token URL are https, an audience is set and the introspection client secret is
/// present (see <see cref="WayfarerSecurityBaseline"/>). This host does not invent an audience name.
/// </summary>
public sealed class WayfarerAegisOptions
{
    public const string Section = "Aegis";

    /// <summary>Aegis introspection contract: the call authenticates as <c>{platform}-introspection</c>.</summary>
    public const string DefaultIntrospectionClientId = "wayfarer-introspection";

    public const int MinimumJwksRefreshSeconds = 30;
    public const int DefaultJwksRefreshMinSeconds = 300;

    public string Issuer { get; set; } = string.Empty;

    public string JwksUrl { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    public string IntrospectionUrl { get; set; } = string.Empty;

    public string IntrospectionClientId { get; set; } = DefaultIntrospectionClientId;

    public string? IntrospectionClientSecret { get; set; }

    public int IntrospectionCacheSeconds { get; set; } = 30;

    public int JwksRefreshMinSeconds { get; set; } = DefaultJwksRefreshMinSeconds;

    /// <summary>Aegis token endpoint for Wayfarer's own client-credentials calls to Titan.</summary>
    public string TokenUrl { get; set; } = string.Empty;

    public bool Configured =>
        !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(JwksUrl) && !string.IsNullOrWhiteSpace(Audience);

    public static bool IsHttps(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);
}
