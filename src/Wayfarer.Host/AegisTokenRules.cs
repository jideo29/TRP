using System.Security.Claims;
using System.Text.Json;

namespace Wayfarer;

/// <summary>
/// Integrated token rules for Wayfarer. Integrated boot stays refused by <see cref="FailClosedGate"/>.
/// <c>customerRef</c> is an ADR-003 object (<c>scheme</c>, <c>id</c>, <c>tenantId</c>) and is not <c>sub</c>.
/// Audience <c>Vulcan.Api</c> does not require <c>customerRef</c>. This host does not invent an audience name.
/// </summary>
public static class AegisTokenRules
{
    public const string CustomerRefClaim = "customerRef";
    public const string VulcanAudience = "Vulcan.Api";
    public const string AccessTokenType = "at+jwt";
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    public static bool TryAccept(ClaimsIdentity? identity, out string failure)
    {
        if (identity is null)
        {
            failure = "Aegis access token did not produce an authenticated principal. Fail closed.";
            return false;
        }

        var subject = identity.FindFirst("sub")?.Value?.Trim();
        if (string.IsNullOrEmpty(subject))
        {
            failure = "Aegis access token is missing sub. Fail closed.";
            return false;
        }

        var tenant = identity.FindFirst("tenantId")?.Value?.Trim();
        var legacy = identity.FindFirst("tenant_id")?.Value?.Trim();
        if (string.IsNullOrEmpty(tenant) && string.IsNullOrEmpty(legacy))
        {
            failure = "Aegis access token is missing tenantId. Fail closed.";
            return false;
        }

        if (!string.IsNullOrEmpty(tenant) && !string.IsNullOrEmpty(legacy)
            && !string.Equals(tenant, legacy, StringComparison.Ordinal))
        {
            failure = "Aegis access token tenantId and tenant_id disagree. Fail closed.";
            return false;
        }

        var audiences = identity.FindAll("aud")
            .Select(claim => claim.Value?.Trim())
            .Where(value => !string.IsNullOrEmpty(value))
            .ToArray();
        if (audiences.Length == 1 && string.Equals(audiences[0], VulcanAudience, StringComparison.Ordinal))
        {
            failure = string.Empty;
            return true;
        }

        return TryReadCustomerRef(identity, out failure);
    }

    private static bool TryReadCustomerRef(ClaimsIdentity identity, out string failure)
    {
        var found = 0;
        var sawClaim = false;
        foreach (var claim in identity.FindAll(CustomerRefClaim))
        {
            sawClaim = true;
            if (string.IsNullOrWhiteSpace(claim.Value))
            {
                failure = "Aegis access token customerRef lacks scheme, id, or tenantId. Fail closed.";
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(claim.Value);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (!HasFields(doc.RootElement))
                    {
                        failure = "Aegis access token customerRef lacks scheme, id, or tenantId. Fail closed.";
                        return false;
                    }

                    found++;
                    continue;
                }

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !HasFields(item))
                        {
                            failure = "Aegis access token customerRef lacks scheme, id, or tenantId. Fail closed.";
                            return false;
                        }

                        found++;
                    }

                    continue;
                }
            }
            catch (JsonException)
            {
                failure = "Aegis access token customerRef is not an ADR-003 object. Fail closed.";
                return false;
            }

            failure = "Aegis access token customerRef is not an ADR-003 object. Fail closed.";
            return false;
        }

        if (!sawClaim || found == 0)
        {
            failure = "Aegis access token is missing customerRef. Fail closed.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool HasFields(JsonElement obj) =>
        HasText(obj, "scheme") && HasText(obj, "id") && HasText(obj, "tenantId");

    private static bool HasText(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(prop.GetString());
}
