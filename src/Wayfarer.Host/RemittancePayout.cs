using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Wayfarer;

/// <summary>
/// Payout handoff toward Pulse. This is not a Titan posting and not a payroll path.
/// Integrated mode fails closed while Pulse, Atlas, or Sentinel is unset.
/// Standalone with all three unset returns a stand-in labeled SIMULATED.
/// No result claims settlement, bank_booked, or money_pass.
/// </summary>
public sealed class RemittancePayout
{
    public const string SimulatedLabel = "SIMULATED";
    public const string NotConfiguredLabel = "NOT_CONFIGURED";
    public const string TowardPulseLabel = "TOWARD_PULSE";
    public const string RefusedLabel = "REFUSED";

    private readonly IConfiguration _configuration;
    private readonly HttpMessageHandler? _handler;

    public RemittancePayout(IConfiguration configuration, HttpMessageHandler? handler = null)
    {
        _configuration = configuration;
        _handler = handler;
    }

    public Task<PayoutRefusal> ExecuteAsync(RemittancePayoutInput input, CancellationToken cancellationToken) =>
        ExecuteAsync(_configuration, input, _handler, cancellationToken);

    public static async Task<RemittancePayoutBody?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength is 0)
            return null;

        string text;
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            text = await reader.ReadToEndAsync(cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RemittancePayoutBody>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<PayoutRefusal> ExecuteAsync(
        IConfiguration configuration,
        RemittancePayoutInput input,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!HasCustomerRef(input.Scheme, input.Id, input.TenantId))
            return RemittanceSystemOfRecord.RefusePayout();

        var mode = configuration["Deployment:Mode"];
        var pulseUrl = configuration["Pulse:BaseUrl"];
        var atlasUrl = configuration["Atlas:BaseUrl"];
        var sentinelUrl = configuration["Sentinel:BaseUrl"];
        var pulseSet = IsAbsoluteHttp(pulseUrl);
        var atlasSet = IsAbsoluteHttp(atlasUrl);
        var sentinelSet = IsAbsoluteHttp(sentinelUrl);

        if (!pulseSet && !atlasSet && !sentinelSet)
        {
            return TitanCustomerLedgerBind.IsStandalone(mode)
                ? Simulated("Standalone stand-in. No Pulse, Atlas, or Sentinel call was made.")
                : NotConfigured("Integrated mode fails closed while Pulse, Atlas, and Sentinel are unset.");
        }

        if (!pulseSet || !atlasSet || !sentinelSet)
        {
            return TitanCustomerLedgerBind.IsStandalone(mode)
                ? Refused("A configured peer cannot run beside an unset Pulse, Atlas, or Sentinel URL.")
                : NotConfigured("Integrated mode fails closed while Pulse, Atlas, or Sentinel is unset.");
        }

        var atlas = await AtlasEntitlementConsult.ConsultAsync(
            atlasUrl,
            configuration["Atlas:Environment"],
            input.TenantId!,
            input.Bearer,
            handler,
            cancellationToken);
        if (!atlas.Allowed)
            return FromPeer(atlas.Outcome, atlas.Detail);

        var risk = await SentinelRiskCall.DecideAsync(
            sentinelUrl,
            input.Scheme!,
            input.Id!,
            input.TenantId!,
            input.Bearer,
            input.CorrelationId,
            handler,
            cancellationToken);
        if (!risk.Allowed)
            return FromPeer(risk.Outcome, risk.Detail);

        var pulse = await PulsePayoutPort.SubmitAsync(
            pulseUrl,
            input.Rail,
            input.AmountPhp,
            input.Bearer,
            input.CorrelationId,
            handler,
            cancellationToken);
        return FromPeer(pulse.Outcome, pulse.Detail);
    }

    private static PayoutRefusal FromPeer(PeerOutcome outcome, string detail) => outcome switch
    {
        PeerOutcome.NotConfigured => NotConfigured(detail),
        PeerOutcome.Simulated => Simulated(detail),
        PeerOutcome.TowardPulse => TowardPulse(detail),
        _ => Refused(detail)
    };

    private static PayoutRefusal Simulated(string detail) => Closed(
        StatusCodes.Status503ServiceUnavailable,
        "REMITTANCE_SIMULATED",
        SimulatedLabel,
        "SIMULATED stand-in. Live payout is not implemented. " + detail + " "
            + RemittanceSystemOfRecord.Statement
            + " This is not settlement, bank_booked, or money_pass. The remittance journey is not unblocked.");

    private static PayoutRefusal NotConfigured(string detail) => Closed(
        StatusCodes.Status503ServiceUnavailable,
        "REMITTANCE_NOT_CONFIGURED",
        NotConfiguredLabel,
        "NOT_CONFIGURED. " + detail + " " + RemittanceSystemOfRecord.Statement
            + " This is not settlement, bank_booked, or money_pass.");

    private static PayoutRefusal TowardPulse(string detail) => Closed(
        StatusCodes.Status503ServiceUnavailable,
        "REMITTANCE_TOWARD_PULSE",
        TowardPulseLabel,
        "Payout was handed toward Pulse. " + detail + " "
            + RemittanceSystemOfRecord.Statement
            + " Wayfarer does not claim settlement, bank_booked, or money_pass. The remittance journey is not unblocked.");

    private static PayoutRefusal Refused(string detail) => Closed(
        StatusCodes.Status503ServiceUnavailable,
        "REMITTANCE_REFUSED",
        RefusedLabel,
        "REFUSED. " + detail + " " + RemittanceSystemOfRecord.Statement
            + " This is not settlement, bank_booked, or money_pass.");

    private static PayoutRefusal Closed(int status, string code, string label, string message) => new(
        StatusCode: status,
        Code: code,
        Message: message,
        PayoutExecuted: false,
        MoneyPass: false,
        Label: label,
        BankBooked: false,
        SettlementClaimed: false);

    internal static bool HasCustomerRef(string? scheme, string? id, string? tenantId) =>
        !string.IsNullOrWhiteSpace(scheme)
        && !string.IsNullOrWhiteSpace(id)
        && !string.IsNullOrWhiteSpace(tenantId)
        && (scheme.Trim() is "titan-cif" or "foreign-cif");

    internal static bool IsAbsoluteHttp(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed record RemittancePayoutInput(
    string? Scheme,
    string? Id,
    string? TenantId,
    string? Rail,
    decimal? AmountPhp,
    string? Bearer,
    string? CorrelationId);

public enum PeerOutcome
{
    NotConfigured,
    Simulated,
    Allowed,
    TowardPulse,
    Refused
}

public sealed record PeerCallResult(PeerOutcome Outcome, bool Allowed, string Detail);

public static class AtlasEntitlementConsult
{
    public const string Platform = "wayfarer";
    public const string Module = "remittance";
    public const string CapabilityId = "payout";

    public static async Task<PeerCallResult> ConsultAsync(
        string? baseUrl,
        string? environment,
        string tenantId,
        string? bearer,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!RemittancePayout.IsAbsoluteHttp(baseUrl))
        {
            return new PeerCallResult(PeerOutcome.NotConfigured, false, "Atlas base URL is unset.");
        }

        if (string.IsNullOrWhiteSpace(bearer))
            return new PeerCallResult(PeerOutcome.Refused, false, "Atlas consult requires a bearer token.");

        if (environment is not ("Development" or "Staging" or "Production"))
            return new PeerCallResult(PeerOutcome.Refused, false, "Atlas environment is unset.");

        var query = string.Join("&", new[]
        {
            "tenantId=" + Uri.EscapeDataString(tenantId),
            "platform=" + Uri.EscapeDataString(Platform),
            "module=" + Uri.EscapeDataString(Module),
            "capabilityId=" + Uri.EscapeDataString(CapabilityId),
            "environment=" + Uri.EscapeDataString(environment)
        });

        try
        {
            var response = await PeerHttp.SendAsync(
                handler,
                baseUrl!,
                HttpMethod.Get,
                "v1/consultation?" + query,
                bearer,
                null,
                null,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return new PeerCallResult(PeerOutcome.Refused, false, "Atlas consult failed closed.");

            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var decision = ReadString(root, "decision");
            var allowed = root.TryGetProperty("isAllowed", out var flag)
                && flag.ValueKind == JsonValueKind.True;
            if (string.Equals(decision, "Allow", StringComparison.Ordinal) && allowed)
                return new PeerCallResult(PeerOutcome.Allowed, true, "Atlas Allow.");

            return new PeerCallResult(PeerOutcome.Refused, false, "Atlas did not Allow. Unknown and Deny fail closed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PeerCallResult(PeerOutcome.Refused, false, "Atlas consult failed closed.");
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class SentinelRiskCall
{
    public static async Task<PeerCallResult> DecideAsync(
        string? baseUrl,
        string scheme,
        string id,
        string tenantId,
        string? bearer,
        string? correlationId,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!RemittancePayout.IsAbsoluteHttp(baseUrl))
            return new PeerCallResult(PeerOutcome.NotConfigured, false, "Sentinel base URL is unset.");

        if (string.IsNullOrWhiteSpace(bearer))
            return new PeerCallResult(PeerOutcome.Refused, false, "Sentinel risk call requires a bearer token.");

        var decisionId = Guid.NewGuid().ToString("D");
        var payload = JsonSerializer.Serialize(new
        {
            decisionId,
            tenantId,
            schemaVersion = "1",
            callingPlatform = "wayfarer",
            subject = new { scheme, id, tenantId },
            action = "remittance.payout",
            correlationId = string.IsNullOrWhiteSpace(correlationId) ? decisionId : correlationId
        });

        try
        {
            var response = await PeerHttp.SendAsync(
                handler,
                baseUrl!,
                HttpMethod.Post,
                "api/v1/risk/decisions",
                bearer,
                correlationId,
                payload,
                cancellationToken,
                decisionId);
            if (response.StatusCode != HttpStatusCode.OK)
                return new PeerCallResult(PeerOutcome.Refused, false, "Sentinel risk call failed closed.");

            using var document = JsonDocument.Parse(response.Body);
            var outcome = document.RootElement.TryGetProperty("outcome", out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
            if (string.Equals(outcome, "Allow", StringComparison.Ordinal))
                return new PeerCallResult(PeerOutcome.Allowed, true, "Sentinel Allow.");

            return new PeerCallResult(PeerOutcome.Refused, false, "Sentinel did not Allow.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PeerCallResult(PeerOutcome.Refused, false, "Sentinel risk call failed closed.");
        }
    }
}

public static class PulsePayoutPort
{
    public static async Task<PeerCallResult> SubmitAsync(
        string? baseUrl,
        string? rail,
        decimal? amountPhp,
        string? bearer,
        string? correlationId,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        if (!RemittancePayout.IsAbsoluteHttp(baseUrl))
            return new PeerCallResult(PeerOutcome.NotConfigured, false, "Pulse base URL is unset.");

        if (string.IsNullOrWhiteSpace(bearer))
            return new PeerCallResult(PeerOutcome.Refused, false, "Pulse payout requires a bearer token.");

        var selected = rail?.Trim().ToLowerInvariant();
        string path;
        string payload;
        if (selected == "instapay")
        {
            if (amountPhp is null || amountPhp <= 0)
                return new PeerCallResult(PeerOutcome.Refused, false, "InstaPay amount is required.");
            path = "instapay/transfers";
            payload = JsonSerializer.Serialize(new { amountPhp });
        }
        else if (selected == "pesonet")
        {
            if (amountPhp is null || amountPhp <= 0)
                return new PeerCallResult(PeerOutcome.Refused, false, "PESONet amount is required.");
            path = "pesonet/batches/process";
            payload = JsonSerializer.Serialize(new
            {
                businessDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
                amountsPhp = new[] { amountPhp },
                clearingPayloadXml = (string?)null
            });
        }
        else
        {
            return new PeerCallResult(
                PeerOutcome.Refused,
                false,
                "Rail must be instapay or pesonet. Wayfarer does not choose a rail and does not post to Titan.");
        }

        if (payload.Contains("score", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("bank_booked", StringComparison.OrdinalIgnoreCase)
            || payload.Contains("money_pass", StringComparison.OrdinalIgnoreCase))
        {
            return new PeerCallResult(PeerOutcome.Refused, false, "Payout payload refused.");
        }

        try
        {
            var response = await PeerHttp.SendAsync(
                handler,
                baseUrl!,
                HttpMethod.Post,
                path,
                bearer,
                correlationId,
                payload,
                cancellationToken,
                Guid.NewGuid().ToString("D"));
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                return new PeerCallResult(PeerOutcome.Refused, false, "Pulse did not accept the handoff.");

            return new PeerCallResult(PeerOutcome.TowardPulse, true, "Pulse accepted the handoff on " + selected + ".");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PeerCallResult(PeerOutcome.Refused, false, "Pulse payout failed closed.");
        }
    }
}

internal sealed record PeerHttpResponse(HttpStatusCode StatusCode, string Body);

internal static class PeerHttp
{
    public static async Task<PeerHttpResponse> SendAsync(
        HttpMessageHandler? handler,
        string baseUrl,
        HttpMethod method,
        string relative,
        string? bearer,
        string? correlationId,
        string? json,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, relative);
        if (!string.IsNullOrWhiteSpace(bearer))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Trim());
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new PeerHttpResponse(response.StatusCode, body);
    }
}
