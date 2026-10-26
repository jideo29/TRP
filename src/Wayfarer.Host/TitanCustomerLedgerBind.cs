using System.Net;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Wayfarer.Security;

namespace Wayfarer;

/// <summary>
/// Resolves an ADR-003 customer ref and a Titan ledger account ref.
/// This bind does not submit a posting instruction and does not write a local ledger row.
/// L20 / ASVS 1.2.2: every Titan call carries Wayfarer's own client-credentials token
/// (<see cref="ITitanAccessTokenSource"/>); without one no Titan call is made and the result is NotConfigured.
/// ASVS 8.3.1: the CustomerRef travels only in the JSON body of Titan's CustomerDirectoryPort lookups
/// (<c>POST api/customer-directory/refs/lookup</c> and <c>POST api/customer-directory/ledger-account/lookup</c>,
/// BiaB <c>docs/wave-a-customer-directory-port.md</c>); never in a URL or query string. Every error fails closed.
/// </summary>
public interface ITitanCustomerLedgerBind
{
    Task<TitanCustomerBindResult> ResolveCustomerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken);

    Task<TitanLedgerBindResult> ResolveLedgerAsync(string scheme, string id, string tenantId, string? currencyCode, CancellationToken cancellationToken);
}

public enum TitanBindOutcome
{
    NotConfigured,
    StandIn,
    Bound,
    Refused,
}

public sealed record TitanCustomerBindResult(
    TitanBindOutcome Outcome,
    string Code,
    string Label,
    string? Scheme,
    string? Id,
    string? TenantId,
    bool Synthetic);

public sealed record TitanLedgerBindResult(
    TitanBindOutcome Outcome,
    string Code,
    string Label,
    string? LedgerAccountRef,
    bool LedgerRowWritten);

public sealed class TitanCustomerLedgerBind : ITitanCustomerLedgerBind
{
    public const string CustomerStandInLabel = "TITAN_CUSTOMER_STANDALONE_STAND_IN";
    public const string LedgerStandInLabel = "TITAN_LEDGER_STANDALONE_STAND_IN";
    public const string CustomerRefLookupPath = "api/customer-directory/refs/lookup";
    public const string LedgerAccountLookupPath = "api/customer-directory/ledger-account/lookup";
    public const string TenantMismatchCode = "TENANT_MISMATCH";
    public const string LedgerAccountAmbiguousCode = "LEDGER_ACCOUNT_AMBIGUOUS";

    private readonly string? _mode;
    private readonly string? _customerBaseUrl;
    private readonly string? _ledgerBaseUrl;
    private readonly HttpMessageHandler? _handler;
    private readonly ITitanAccessTokenSource? _tokens;

    public TitanCustomerLedgerBind(IConfiguration configuration, HttpMessageHandler? handler = null, ITitanAccessTokenSource? tokens = null)
    {
        _mode = configuration["Deployment:Mode"];
        _customerBaseUrl = configuration[TitanClientCredentials.CustomerDirectoryUrlSetting];
        _ledgerBaseUrl = configuration[TitanClientCredentials.LedgerUrlSetting];
        _handler = handler;
        _tokens = tokens;
    }

    public async Task<TitanCustomerBindResult> ResolveCustomerAsync(
        string scheme,
        string id,
        string tenantId,
        CancellationToken cancellationToken) =>
        await ResolveCustomerAsync(_mode, _customerBaseUrl, scheme, id, tenantId,
            await TokenAsync(_customerBaseUrl, cancellationToken), _handler, cancellationToken);

    public async Task<TitanLedgerBindResult> ResolveLedgerAsync(
        string scheme,
        string id,
        string tenantId,
        string? currencyCode,
        CancellationToken cancellationToken) =>
        await ResolveLedgerAsync(_mode, _ledgerBaseUrl, scheme, id, tenantId, currencyCode,
            await TokenAsync(_ledgerBaseUrl, cancellationToken), _handler, cancellationToken);

    private async Task<string?> TokenAsync(string? baseUrl, CancellationToken cancellationToken) =>
        IsAbsoluteHttp(baseUrl) && _tokens is not null ? await _tokens.GetAsync(cancellationToken) : null;

    /// <summary>L20: the ledger reference is withheld from a caller whose token does not name this customer.</summary>
    public static TitanLedgerBindResult LedgerWithheld() =>
        new(TitanBindOutcome.Refused, "titan.ledger.not_entitled", "WITHHELD", null, false);

    public static bool IsStandalone(string? mode) =>
        string.Equals(mode?.Trim(), "Standalone", StringComparison.OrdinalIgnoreCase);

    public static async Task<TitanCustomerBindResult> ResolveCustomerAsync(
        string? mode,
        string? baseUrl,
        string scheme,
        string id,
        string tenantId,
        string? accessToken,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!HasRef(scheme, id, tenantId))
        {
            return CustomerRefused("titan.customer.refused");
        }

        if (!IsAbsoluteHttp(baseUrl))
        {
            return IsStandalone(mode)
                ? new TitanCustomerBindResult(TitanBindOutcome.StandIn, "titan.customer.standalone_stand_in", CustomerStandInLabel, scheme, id, tenantId, true)
                : new TitanCustomerBindResult(TitanBindOutcome.NotConfigured, "titan.customer.not_configured", "NOT_CONFIGURED", null, null, null, false);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new TitanCustomerBindResult(TitanBindOutcome.NotConfigured, "titan.customer.credential_missing", "NOT_CONFIGURED", null, null, null, false);
        }

        try
        {
            using var response = await PostAsync(
                handler,
                baseUrl!,
                accessToken,
                CustomerRefLookupPath,
                new Dictionary<string, string> { ["scheme"] = scheme, ["id"] = id, ["tenantId"] = tenantId },
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return CustomerRefused("titan.customer.not_found");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden && ErrorCode(body) == TenantMismatchCode)
            {
                return CustomerRefused("titan.customer.tenant_mismatch");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return CustomerRefused("titan.customer.refused");
            }

            using var document = JsonDocument.Parse(body);
            if (!TryReadData(document.RootElement, out var data)
                || !SameRef(data, scheme, id, tenantId))
            {
                return CustomerRefused("titan.customer.refused");
            }

            return new TitanCustomerBindResult(TitanBindOutcome.Bound, "titan.customer.bound", "TITAN", scheme, id, tenantId, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return CustomerRefused("titan.customer.refused");
        }
    }

    public static async Task<TitanLedgerBindResult> ResolveLedgerAsync(
        string? mode,
        string? baseUrl,
        string scheme,
        string id,
        string tenantId,
        string? currencyCode,
        string? accessToken,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!HasRef(scheme, id, tenantId))
        {
            return LedgerRefused("titan.ledger.refused");
        }

        if (!IsAbsoluteHttp(baseUrl))
        {
            return IsStandalone(mode)
                ? new TitanLedgerBindResult(TitanBindOutcome.StandIn, "titan.ledger.standalone_stand_in", LedgerStandInLabel, LedgerStandInLabel, false)
                : new TitanLedgerBindResult(TitanBindOutcome.NotConfigured, "titan.ledger.not_configured", "NOT_CONFIGURED", null, false);
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new TitanLedgerBindResult(TitanBindOutcome.NotConfigured, "titan.ledger.credential_missing", "NOT_CONFIGURED", null, false);
        }

        try
        {
            var request = new Dictionary<string, string> { ["scheme"] = scheme, ["id"] = id, ["tenantId"] = tenantId };
            if (!string.IsNullOrWhiteSpace(currencyCode))
            {
                request["currencyCode"] = currencyCode.Trim();
            }

            using var response = await PostAsync(handler, baseUrl!, accessToken, LedgerAccountLookupPath, request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return LedgerRefused("titan.ledger.not_found");
            }

            if (response.StatusCode == HttpStatusCode.Conflict && ErrorCode(body) == LedgerAccountAmbiguousCode)
            {
                return LedgerRefused("titan.ledger.ambiguous");
            }

            if (response.StatusCode == HttpStatusCode.Forbidden && ErrorCode(body) == TenantMismatchCode)
            {
                return LedgerRefused("titan.ledger.tenant_mismatch");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return LedgerRefused("titan.ledger.refused");
            }

            using var document = JsonDocument.Parse(body);
            if (!TryReadData(document.RootElement, out var data) || !SameRef(data, scheme, id, tenantId))
            {
                return LedgerRefused("titan.ledger.refused");
            }

            var ledgerAccountRef = ReadString(data, "ledgerAccountRef");
            if (string.IsNullOrWhiteSpace(ledgerAccountRef))
            {
                return LedgerRefused("titan.ledger.refused");
            }

            return new TitanLedgerBindResult(TitanBindOutcome.Bound, "titan.ledger.bound", "TITAN", ledgerAccountRef, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return LedgerRefused("titan.ledger.refused");
        }
    }

    private static TitanCustomerBindResult CustomerRefused(string code) =>
        new(TitanBindOutcome.Refused, code, "REFUSED", null, null, null, false);

    private static TitanLedgerBindResult LedgerRefused(string code) =>
        new(TitanBindOutcome.Refused, code, "REFUSED", null, false);

    private static async Task<HttpResponseMessage> PostAsync(
        HttpMessageHandler? handler,
        string baseUrl,
        string accessToken,
        string relative,
        IReadOnlyDictionary<string, string> body,
        CancellationToken cancellationToken)
    {
        // Redirects are not followed: a 3xx is not 200 and fails closed, and the body is never re-sent elsewhere.
        using var client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Post, relative)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, cancellationToken);
    }

    /// <summary>Titan wraps a success in <c>{ "data": ... }</c>.</summary>
    private static bool TryReadData(JsonElement root, out JsonElement data)
    {
        data = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Object;
    }

    /// <summary>The answer must be for the CustomerRef that was asked about.</summary>
    private static bool SameRef(JsonElement data, string scheme, string id, string tenantId) =>
        string.Equals(ReadString(data, "scheme"), scheme, StringComparison.Ordinal)
        && string.Equals(ReadString(data, "id"), id, StringComparison.Ordinal)
        && string.Equals(ReadString(data, "tenantId"), tenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Titan errors are <c>{ "error": { "code": ... } }</c>; anything else has no code.</summary>
    private static string? ErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                    ? ReadString(error, "code")
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasRef(string scheme, string id, string tenantId) =>
        !string.IsNullOrWhiteSpace(scheme) && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(tenantId);

    private static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
