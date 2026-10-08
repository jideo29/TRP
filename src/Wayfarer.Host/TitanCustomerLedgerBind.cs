using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Wayfarer;

/// <summary>
/// Resolves an ADR-003 customer ref and a Titan ledger account ref.
/// This bind does not submit a posting instruction and does not write a local ledger row.
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

    public TitanCustomerLedgerBind(IConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _mode = configuration["Deployment:Mode"];
        _customerBaseUrl = configuration["Titan:CustomerDirectory:BaseUrl"];
        _ledgerBaseUrl = configuration["Titan:Ledger:BaseUrl"];
        _handler = handler;
    }

    public Task<TitanCustomerBindResult> ResolveCustomerAsync(
        string scheme,
        string id,
        string tenantId,
        CancellationToken cancellationToken) =>
        ResolveCustomerAsync(_mode, _customerBaseUrl, scheme, id, tenantId, _handler, cancellationToken);

    public Task<TitanLedgerBindResult> ResolveLedgerAsync(
        string scheme,
        string id,
        string tenantId,
        CancellationToken cancellationToken) =>
        ResolveLedgerAsync(_mode, _ledgerBaseUrl, scheme, id, tenantId, _handler, cancellationToken);

    public static bool IsStandalone(string? mode) =>
        string.Equals(mode?.Trim(), "Standalone", StringComparison.OrdinalIgnoreCase);

    public static async Task<TitanCustomerBindResult> ResolveCustomerAsync(
        string? mode,
        string? baseUrl,
        string scheme,
        string id,
        string tenantId,
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

        try
        {
            using var response = await SendAsync(
                handler,
                baseUrl!,
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

        try
        {
            using var response = await SendAsync(
                handler,
                baseUrl!,
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
        string relative,
        CancellationToken cancellationToken)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        return await client.GetAsync(relative, cancellationToken);
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
