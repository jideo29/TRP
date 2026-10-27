using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Wayfarer.Security;

/// <summary>Supplies the access token Wayfarer presents to Titan.</summary>
public interface ITitanAccessTokenSource
{
    /// <summary>A Wayfarer machine token, or null when none can be obtained (the Titan call is then not made).</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// L20 / ASVS 1.2.2: Wayfarer calls the Titan customer directory and ledger with its own Aegis client-credentials
/// token (<c>Titan:ClientId</c> / <c>Titan:ClientSecret</c> against <c>Aegis:TokenUrl</c>, optional
/// <c>Titan:Scope</c>), never with the caller's token and never without credentials. The client authenticates with
/// HTTP Basic. The token is cached until 30 seconds before it expires. With no credential, or when Aegis does not
/// answer, the result is null and the lookup fails closed.
/// </summary>
public sealed class TitanClientCredentials : ITitanAccessTokenSource
{
    public const string CustomerDirectoryUrlSetting = "Titan:CustomerDirectory:BaseUrl";
    public const string LedgerUrlSetting = "Titan:Ledger:BaseUrl";
    public const string ClientIdSetting = "Titan:ClientId";
    public const string ClientSecretSetting = "Titan:ClientSecret";
    public const string ScopeSetting = "Titan:Scope";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly IOptions<WayfarerAegisOptions> _aegis;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public TitanClientCredentials(HttpClient http, IConfiguration configuration, IOptions<WayfarerAegisOptions> aegis, TimeProvider clock)
    {
        _http = http;
        _configuration = configuration;
        _aegis = aegis;
        _clock = clock;
    }

    public static bool TitanConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[CustomerDirectoryUrlSetting])
        || !string.IsNullOrWhiteSpace(configuration[LedgerUrlSetting]);

    public static bool CredentialsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[ClientIdSetting])
        && !string.IsNullOrWhiteSpace(configuration[ClientSecretSetting])
        && !string.IsNullOrWhiteSpace(configuration["Aegis:TokenUrl"]);

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        if (!CredentialsConfigured(_configuration))
            return null;

        if (_token is not null && _clock.GetUtcNow() < _expiresAt - RefreshMargin)
            return _token;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token is not null && _clock.GetUtcNow() < _expiresAt - RefreshMargin)
                return _token;

            var fields = new List<KeyValuePair<string, string>> { new("grant_type", "client_credentials") };
            var scope = _configuration[ScopeSetting];
            if (!string.IsNullOrWhiteSpace(scope))
                fields.Add(new("scope", scope.Trim()));

            using var request = new HttpRequestMessage(HttpMethod.Post, _aegis.Value.TokenUrl.Trim())
            {
                Content = new FormUrlEncodedContent(fields),
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                Uri.EscapeDataString(_configuration[ClientIdSetting]!.Trim()) + ":"
                + Uri.EscapeDataString(_configuration[ClientSecretSetting]!)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(token.GetString()))
            {
                return null;
            }

            var lifetime = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMinutes(1);
            _token = token.GetString();
            _expiresAt = _clock.GetUtcNow() + lifetime;
            return _token;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                              or InvalidOperationException or UriFormatException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
