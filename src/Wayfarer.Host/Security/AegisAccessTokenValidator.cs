using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Wayfarer.Security;

public enum AegisTokenFailure
{
    None,
    Rejected,
    BadSignature,
    UnknownKey,
    JwksUnreachable,
    WrongAudience,
    WrongType,
    ClaimShape,
    Revoked,
    RevocationUnavailable,
}

public sealed record AegisTokenDecision(
    bool Accepted,
    AegisTokenFailure Failure,
    ClaimsIdentity? Identity = null,
    DateTimeOffset? ExpiresAt = null,
    string? TokenId = null,
    string? Detail = null);

/// <summary>
/// M52 / ASVS 3.5.3: validates an Aegis access token before any peer call. The signature must verify against the
/// Aegis JWKS (ES256, which Aegis signs with on P-256, or RS256 if an RSA key is published; nothing else), the issuer and the configured audience must match, <c>exp</c> is required and checked
/// (30 s skew), and <c>typ</c> must be <c>at+jwt</c>. The claim shape is then checked by
/// <see cref="AegisTokenRules.TryAccept"/> (<c>sub</c>, <c>tenantId</c>, ADR-003 <c>customerRef</c>).
/// Nothing is read from an unvalidated payload.
/// </summary>
public sealed class AegisAccessTokenValidator
{
    public static readonly IReadOnlyList<string> AllowedAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256];

    private readonly HttpClient _http;
    private readonly AegisSigningKeyCache _keys;

    public AegisAccessTokenValidator(HttpClient http, AegisSigningKeyCache keys)
    {
        _http = http;
        _keys = keys;
    }

    public async Task<AegisTokenDecision> ValidateAsync(string? token, WayfarerAegisOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || !options.Configured)
            return Reject(AegisTokenFailure.Rejected);

        string? kid;
        string? alg;
        try
        {
            var unvalidated = new JsonWebToken(token);
            kid = unvalidated.Kid;
            alg = unvalidated.Alg;
        }
        catch (Exception exception) when (exception is ArgumentException or SecurityTokenMalformedException)
        {
            return Reject(AegisTokenFailure.Rejected);
        }

        if (!AllowedAlgorithms.Contains(alg, StringComparer.Ordinal))
            return Reject(AegisTokenFailure.BadSignature);

        var keys = await _keys.GetAsync(_http, options.JwksUrl, kid, cancellationToken).ConfigureAwait(false);
        if (keys is null || keys.Count == 0)
            return Reject(AegisTokenFailure.JwksUnreachable);
        if (!string.IsNullOrEmpty(kid) && !keys.Any(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal)))
            return Reject(AegisTokenFailure.UnknownKey);

        TokenValidationResult result;
        try
        {
            result = await new JsonWebTokenHandler { MapInboundClaims = false }.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = options.Issuer.Trim(),
                ValidateAudience = true,
                ValidAudience = options.Audience.Trim(),
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys,
                ValidAlgorithms = AllowedAlgorithms,
                ClockSkew = AegisTokenRules.ClockSkew,
                NameClaimType = "sub",
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Reject(AegisTokenFailure.Rejected);
        }

        if (!result.IsValid)
            return Reject(Classify(result.Exception));

        if (result.SecurityToken is not JsonWebToken accessToken
            || !string.Equals(accessToken.Typ, AegisTokenRules.AccessTokenType, StringComparison.Ordinal))
        {
            return Reject(AegisTokenFailure.WrongType);
        }

        var identity = new ClaimsIdentity(result.ClaimsIdentity.Claims, WayfarerAuthentication.AegisScheme, "sub", "roles");
        if (!AegisTokenRules.TryAccept(identity, out var failure))
            return new AegisTokenDecision(false, AegisTokenFailure.ClaimShape, Detail: failure);

        return new AegisTokenDecision(
            true,
            AegisTokenFailure.None,
            identity,
            new DateTimeOffset(DateTime.SpecifyKind(accessToken.ValidTo, DateTimeKind.Utc)),
            string.IsNullOrWhiteSpace(accessToken.Id) ? null : accessToken.Id);
    }

    private static AegisTokenFailure Classify(Exception? exception)
    {
        var name = exception?.GetType().Name ?? string.Empty;
        if (name.Contains("Audience", StringComparison.Ordinal))
            return AegisTokenFailure.WrongAudience;
        if (name.Contains("Signature", StringComparison.Ordinal))
            return AegisTokenFailure.BadSignature;
        return AegisTokenFailure.Rejected;
    }

    private static AegisTokenDecision Reject(AegisTokenFailure failure) => new(false, failure);
}
