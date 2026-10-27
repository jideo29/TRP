using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Wayfarer.Security;

public enum RevocationStatus
{
    Active,
    Revoked,
    Unavailable
}

/// <summary>
/// X2 / ASVS 3.3.1: checks that an Aegis access token is still active through RFC 7662 introspection
/// (<c>Aegis:IntrospectionUrl</c>, form field <c>token</c>, answer <c>active</c>), authenticated with the
/// <c>Aegis:IntrospectionClientId</c> / <c>Aegis:IntrospectionClientSecret</c> client. Aegis contract: the call
/// authenticates as <c>wayfarer-introspection</c> with HTTP Basic; the caller's token is only the <c>token</c> form value,
/// never the Bearer credential, and no tenant header is sent. Without client credentials nothing is sent and the
/// status is Unavailable. Answers are cached by
/// <c>jti</c> (SHA-256 of the token when there is none): active for at most
/// <c>Aegis:IntrospectionCacheSeconds</c> and never past <c>exp</c>; revoked until <c>exp</c>. When Aegis cannot
/// answer the status is <see cref="RevocationStatus.Unavailable"/> and the request is refused (fail closed).
/// </summary>
public sealed class AegisRevocationChecker
{
    private readonly HttpClient _http;
    private readonly WayfarerAegisOptions _options;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;

    public AegisRevocationChecker(
        HttpClient http,
        IOptions<WayfarerAegisOptions> options,
        IMemoryCache cache,
        TimeProvider clock)
    {
        _http = http;
        _options = options.Value;
        _cache = cache;
        _clock = clock;
    }

    /// <summary>True when an introspection URL is set. Startup refuses a missing URL outside Development.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_options.IntrospectionUrl);

    public async Task<RevocationStatus> CheckAsync(string token, string? jti, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return RevocationStatus.Active;

        var key = "aegis-revocation:" + (string.IsNullOrWhiteSpace(jti) ? TokenHash(token) : "jti:" + jti);
        if (_cache.TryGetValue(key, out RevocationStatus cached))
            return cached;

        var status = await IntrospectAsync(token, cancellationToken).ConfigureAwait(false);
        var untilExp = expiresAt - _clock.GetUtcNow();
        if (untilExp <= TimeSpan.Zero || status == RevocationStatus.Unavailable)
            return status;

        var maxActive = TimeSpan.FromSeconds(Math.Max(0, _options.IntrospectionCacheSeconds));
        var ttl = status == RevocationStatus.Revoked ? untilExp : (untilExp < maxActive ? untilExp : maxActive);
        if (ttl > TimeSpan.Zero)
            _cache.Set(key, status, ttl);
        return status;
    }

    private async Task<RevocationStatus> IntrospectAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var clientId = _options.IntrospectionClientId?.Trim();
            var clientSecret = _options.IntrospectionClientSecret;
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                return RevocationStatus.Unavailable;

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.IntrospectionUrl.Trim())
            {
                Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("token", token),
                    new KeyValuePair<string, string>("token_type_hint", "access_token"),
                ])
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(clientId) + ":" + Uri.EscapeDataString(clientSecret)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return RevocationStatus.Unavailable;

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("active", out var active)
                || (active.ValueKind != JsonValueKind.True && active.ValueKind != JsonValueKind.False))
            {
                return RevocationStatus.Unavailable;
            }

            return active.GetBoolean() ? RevocationStatus.Active : RevocationStatus.Revoked;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              or InvalidOperationException or UriFormatException)
        {
            return RevocationStatus.Unavailable;
        }
    }

    private static string TokenHash(string token) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
