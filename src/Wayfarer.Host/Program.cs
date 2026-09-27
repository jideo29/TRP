using System.Text.Json;
using Wayfarer;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);

var app = builder.Build();

app.MapGet("/api/host", () =>
{
    var id = RemittanceSystemOfRecord.Identity;
    return Results.Ok(new
    {
        product = id.Product,
        repository = id.Repository,
        role = id.Role,
        notPulse = id.NotPulse,
        tension = id.Tension,
        statement = id.Statement,
        livePayout = id.LivePayout,
        moneyPass = id.MoneyPass,
        remittanceJourney = id.RemittanceJourney,
        remittanceJourneyUnblocked = id.RemittanceJourneyUnblocked,
        journeyAcceptClaimed = id.JourneyAcceptClaimed,
        pulseRailsAbsorbed = id.PulseRailsAbsorbed,
        novaRemittanceIntentsAbsorbedByPulse = id.NovaRemittanceIntentsAbsorbedByPulse,
        forbiddenPulseRails = id.ForbiddenPulseRails,
        waveDParked = id.WaveDParked,
        equicom = id.Equicom,
        outboundEmail = id.OutboundEmail,
        sellOpen = id.SellOpen,
        riskCompliance = RiskComplianceGate.Honesty
    });
});

app.MapGet("/api/risk/status", () => Results.Ok(RiskComplianceGate.Honesty));

app.MapPost("/api/risk/decide", (RiskDecideRequest? request) =>
{
    var result = RiskComplianceGate.Decide(request, app.Configuration);
    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapPost("/api/compliance/consult", (ComplianceConsultRequest? request) =>
{
    var result = RiskComplianceGate.Consult(request, app.Configuration);
    return Results.Json(result, statusCode: result.StatusCode);
});

app.MapGet("/api/manifest", () =>
{
    var path = FindManifestNear(AppContext.BaseDirectory)
        ?? FindManifestNear(app.Environment.ContentRootPath);

    if (path is null || !File.Exists(path))
    {
        return Results.Json(
            new
            {
                code = RemittanceSystemOfRecord.FailClosedCode,
                message = "manifest.json missing. Remittance SoR honesty cannot soft-Pass.",
                moneyPass = false,
                remittanceJourney = RemittanceSystemOfRecord.RemittanceJourney,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    using var stream = File.OpenRead(path);
    var doc = JsonDocument.Parse(stream);
    return Results.Json(doc.RootElement, statusCode: StatusCodes.Status200OK);
});

app.MapPost("/api/remittance/payout", async (HttpRequest http) =>
{
    PayoutRequest? body = null;
    if (http.ContentLength is > 0)
    {
        body = await http.ReadFromJsonAsync<PayoutRequest>();
    }

    var rail = body?.Rail ?? body?.Channel;

    // T-11 Pulse rail absorption stays explicit and first.
    if (RemittanceSystemOfRecord.IsForbiddenPulseRail(rail))
    {
        var absorption = RemittanceSystemOfRecord.RefusePulseRailAbsorption(rail!);
        return Results.Json(absorption, statusCode: absorption.StatusCode);
    }

    // Unconfigured RiskPort / Aegis / Entitlement → Fail-not-Pass (Unknown ≠ Allow).
    var riskBlocked = RiskComplianceGate.RefusePayoutForRiskCompliance(app.Configuration, rail);
    return Results.Json(riskBlocked, statusCode: riskBlocked.StatusCode);
});

app.MapPost("/api/remittance/intents", async (HttpRequest http) =>
{
    NovaRemittanceIntentRequest? body = null;
    if (http.ContentLength is > 0)
    {
        body = await http.ReadFromJsonAsync<NovaRemittanceIntentRequest>();
    }

    var refusal = RemittanceSystemOfRecord.RefuseNovaIntent(body);
    return Results.Json(refusal, statusCode: refusal.StatusCode);
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "process-up",
    moneyPass = false,
    remittanceJourney = RemittanceSystemOfRecord.RemittanceJourney,
    remittanceJourneyUnblocked = false,
    livePayout = false,
    pulseRailsAbsorbed = false,
    waveDParked = true,
    riskPortStatus = RiskComplianceGate.NotConfigured,
    aegisIdentityStatus = RiskComplianceGate.NotConfigured,
    entitlementStatus = RiskComplianceGate.NotConfigured,
    complianceUnknownIsAllow = false,
    journeyAcceptClaimed = false,
}));

app.Run();

static string? FindManifestNear(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "manifest.json");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        dir = dir.Parent;
    }

    return null;
}

public sealed record PayoutRequest(string? Rail, string? Channel);

public partial class Program;
