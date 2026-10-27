using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wayfarer;

/// <summary>
/// Local session for a Standalone boot. Not an Aegis access token. X1: Standalone is Development-only, so the host
/// accepts these sessions only in Development (<see cref="Security.WayfarerAuthentication"/>). ASVS 3.3.1 / X2: a
/// session carries its issue time and expires after <c>Standalone:SessionMinutes</c> (default 60, at most 480);
/// a session issued in the future is refused. Restarting with a new <c>Standalone:SigningKey</c> revokes every
/// session.
/// </summary>
public static class StandaloneLocalSession
{
    public const string SigningKeyConfig = "Standalone:SigningKey";
    public const string Label = "Wayfarer standalone in-process session. Not an Aegis access token.";
    public const string EntitlementCode = "ENTITLEMENT_STANDALONE_STAND_IN";
    public const string EntitlementLabel = "Standalone in-process entitlement stand-in. Not an Atlas Allow.";
    public const string SessionMinutesConfig = "Standalone:SessionMinutes";
    public const int DefaultSessionMinutes = 60;
    public const int MaximumSessionMinutes = 480;
    private static readonly TimeSpan IssuedAtSkew = TimeSpan.FromSeconds(30);

    public static TimeSpan Lifetime(IConfiguration configuration)
    {
        var minutes = int.TryParse(configuration[SessionMinutesConfig], out var value) && value > 0
            ? Math.Min(value, MaximumSessionMinutes)
            : DefaultSessionMinutes;
        return TimeSpan.FromMinutes(minutes);
    }

    public static bool TryIssue(IConfiguration configuration, string? operatorId, DateTimeOffset now, out string token, out string failure) =>
        TryIssue(configuration, operatorId, null, now, out token, out failure);

    public static bool TryIssue(IConfiguration configuration, string? operatorId, string? tenantId, DateTimeOffset now, out string token, out string failure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var key = configuration[SigningKeyConfig];
        if (string.IsNullOrWhiteSpace(key))
        {
            token = string.Empty;
            failure = "Standalone:SigningKey is not configured.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(operatorId))
        {
            token = string.Empty;
            failure = "operatorId is required.";
            return false;
        }

        var payload = JsonSerializer.Serialize(new SessionPayload(
            operatorId.Trim(),
            now.ToUnixTimeSeconds(),
            Label,
            string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim()));
        var body = Base64Url(Encoding.UTF8.GetBytes(payload));
        token = body + "." + Sign(key, body);
        failure = string.Empty;
        return true;
    }

    public static bool TryRead(IConfiguration configuration, string? token, out string operatorId) =>
        TryRead(configuration, token, DateTimeOffset.UtcNow, out operatorId, out _);

    public static bool TryRead(IConfiguration configuration, string? token, DateTimeOffset now, out string operatorId, out string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        operatorId = string.Empty;
        tenantId = null;
        var key = configuration[SigningKeyConfig];
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(token))
            return false;

        var parts = token.Split('.');
        if (parts.Length != 2)
            return false;
        var expected = Sign(key, parts[0]);
        var actualBytes = Encoding.UTF8.GetBytes(parts[1]);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        if (actualBytes.Length != expectedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            return false;

        try
        {
            var json = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            var payload = JsonSerializer.Deserialize<SessionPayload>(json);
            if (payload is null || payload.Label != Label || string.IsNullOrWhiteSpace(payload.OperatorId))
                return false;
            var issuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt);
            if (issuedAt > now + IssuedAtSkew || now - issuedAt > Lifetime(configuration))
                return false;
            operatorId = payload.OperatorId;
            tenantId = payload.TenantId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Sign(string key, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(body));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    private sealed record SessionPayload(string OperatorId, long IssuedAt, string Label, string? TenantId = null);
}
