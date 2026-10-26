using System.Net;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Wayfarer.Security;

namespace Wayfarer;

/// <summary>
/// Resolves an ADR-003 customer ref and a Titan ledger account ref.
/// This bind does not submit a posting instruction and does not write a local ledger row.
/// L20 / ASVS 1.2.2: every Titan call carries Wayfarer's own client-credentials token
/// (<see cref="ITitanAccessTokenSource"/>); without one no Titan call is made and the result is NotConfigured.
/// The path shape (<c>refs/{scheme}/{id}</c>, <c>accounts/{scheme}/{id}</c>) is Titan's API.
/// </summary>
public interface ITitanCustomerLedgerBind
{
    Task<TitanCustomerBindResult> ResolveCustomerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken);

    Task<TitanLedgerBindResult> ResolveLedgerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken);
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
        CancellationToken cancellationToken) =>
        await ResolveLedgerAsync(_mode, _ledgerBaseUrl, scheme, id, tenantId,
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
            return new TitanCustomerBindResult(TitanBindOutcome.Refused, "titan.customer.refused", "REFUSED", null, null, null, false);
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
            using var response = await SendAsync(
                handler,
                baseUrl!,
                accessToken,
                "api/customer-directory/refs/" + Uri.EscapeDataString(scheme) + "/" + Uri.EscapeDataString(id) + "?tenantId=" + Uri.EscapeDataString(tenantId),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new TitanCustomerBindResult(TitanBindOutcome.Refused, "titan.customer.refused", "REFUSED", null, null, null, false);
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var boundScheme = ReadString(root, "scheme");
            var boundId = ReadString(root, "id");
            var boundTenant = ReadString(root, "tenantId");
            if (!string.Equals(boundScheme, scheme, StringComparison.Ordinal)
                || !string.Equals(boundId, id, StringComparison.Ordinal)
                || !string.Equals(boundTenant, tenantId, StringComparison.Ordinal))
            {
                return new TitanCustomerBindResult(TitanBindOutcome.Refused, "titan.customer.refused", "REFUSED", null, null, null, false);
            }

            return new TitanCustomerBindResult(TitanBindOutcome.Bound, "titan.customer.bound", "TITAN", boundScheme, boundId, boundTenant, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TitanCustomerBindResult(TitanBindOutcome.Refused, "titan.customer.refused", "REFUSED", null, null, null, false);
        }
    }

    public static async Task<TitanLedgerBindResult> ResolveLedgerAsync(
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
            return new TitanLedgerBindResult(TitanBindOutcome.Refused, "titan.ledger.refused", "REFUSED", null, false);
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
            using var response = await SendAsync(
                handler,
                baseUrl!,
                accessToken,
                "api/ledger/accounts/" + Uri.EscapeDataString(scheme) + "/" + Uri.EscapeDataString(id) + "?tenantId=" + Uri.EscapeDataString(tenantId),
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new TitanLedgerBindResult(TitanBindOutcome.Refused, "titan.ledger.refused", "REFUSED", null, false);
            }

            using var document = JsonDocument.Parse(body);
            var ledgerAccountRef = ReadString(document.RootElement, "ledgerAccountRef");
            if (string.IsNullOrWhiteSpace(ledgerAccountRef))
            {
                return new TitanLedgerBindResult(TitanBindOutcome.Refused, "titan.ledger.refused", "REFUSED", null, false);
            }

            return new TitanLedgerBindResult(TitanBindOutcome.Bound, "titan.ledger.bound", "TITAN", ledgerAccountRef, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TitanLedgerBindResult(TitanBindOutcome.Refused, "titan.ledger.refused", "REFUSED", null, false);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpMessageHandler? handler,
        string baseUrl,
        string accessToken,
        string relative,
        CancellationToken cancellationToken)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await client.SendAsync(request, cancellationToken);
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
