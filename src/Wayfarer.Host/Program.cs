using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Wayfarer;
using Wayfarer.Security;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);

// M52 / L20 / X1 / X2: Aegis bearer validation (signature, issuer, audience, algorithm, lifetime) plus revocation,
// a Development-only Standalone session, and a fallback policy that closes every route that does not opt out.
builder.Services.AddWayfarerAuthentication(builder.Configuration);
// X7: limiter partitioned by client and tenant (address when anonymous), after authentication.
builder.Services.AddWayfarerRateLimiting(builder.Configuration, builder.Environment);
// X7: X-Forwarded-For / -Proto only from ForwardedHeaders:KnownProxies / KnownNetworks.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    WayfarerForwardedHeaders.Configure(options, builder.Configuration));
// L11-style last-resort handler: problem details, no exception detail.
builder.Services.AddProblemDetails();

// L20 / ASVS 1.2.2: Titan is called with Wayfarer's own client-credentials token, never the caller's.
builder.Services.AddHttpClient("titan-token", client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<ITitanAccessTokenSource>(sp => new TitanClientCredentials(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("titan-token"),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IOptions<WayfarerAegisOptions>>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<ITitanCustomerLedgerBind>(sp => new TitanCustomerLedgerBind(
    sp.GetRequiredService<IConfiguration>(), null, sp.GetRequiredService<ITitanAccessTokenSource>()));
builder.Services.AddSingleton(sp => new RemittancePayout(sp.GetRequiredService<IConfiguration>()));
builder.AddWayfarerOpenApi();

var app = builder.Build();

// Startup checks run on the final configuration (X1, X3, X5, M52, L20, X7). The host does not start when one fails.
FailClosedGate.EnsureCanBoot(app.Configuration);
WayfarerSecurityBaseline.Validate(app.Configuration, app.Environment);

app.UseExceptionHandler();
app.UseWayfarerForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWayfarerOpenApi();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

var standaloneAllowed = WayfarerAuthentication.StandaloneAllowed(app.Environment, app.Configuration);

app.MapGet("/api/host", () => Results.Ok(RemittanceSystemOfRecord.Identity))
    .AllowAnonymous()
    .WithName("GetHostIdentity")
    .WithTags("Host")
    .WithSummary("Returns the identity of this fail-closed remittance host.")
    .WithDescription("Reads the published identity of this process. It does not enable payout.")
    .Produces<HostIdentity>(StatusCodes.Status200OK);

app.MapPost("/api/remittance/payout", async (
        HttpContext http,
        RemittancePayout payout,
        CancellationToken cancellationToken) =>
{
    var caller = WayfarerCaller.From(http);
    if (caller is null)
        return Results.Unauthorized();

    var body = await RemittancePayout.ReadBodyAsync(http.Request, cancellationToken);

    // M52 / ASVS 4.1.2: the tenant is the validated token's; a body tenant that differs is refused.
    if (caller.TenantId is null)
        return WayfarerProblems.Forbidden("wayfarer.tenant_missing", "The access token carries no tenant.");
    var bodyTenant = body?.CustomerRef?.TenantId?.Trim();
    if (!string.IsNullOrEmpty(bodyTenant) && !string.Equals(bodyTenant, caller.TenantId, StringComparison.Ordinal))
        return WayfarerProblems.Forbidden("wayfarer.tenant_mismatch", "customerRef.tenantId differs from the caller's tenant.");

    var result = await payout.ExecuteAsync(
        new RemittancePayoutInput(
            body?.CustomerRef?.Scheme,
            body?.CustomerRef?.Id,
            caller.TenantId,
            body?.Rail,
            body?.AmountPhp,
            // Only a validated Aegis token is forwarded; a Standalone session never leaves this host.
            caller.AegisAccessToken,
            http.Request.Headers["X-Correlation-Id"].ToString()),
        cancellationToken);
    return Results.Json(result, statusCode: result.StatusCode);
})
    .RequireAuthorization()
    .WithName("RefusePayout")
    .WithTags("Remittance")
    .WithSummary("Payout toward Pulse. No settlement is claimed.")
    .WithDescription(
        "Requires an Aegis Bearer access token. The tenant comes from the token; a customerRef.tenantId that differs is refused with 403. "
        + "Live payout is not implemented. customerRef is required. "
        + "The handoff is POST /corp-pay/orchestrate, the Pulse path Summit uses, with channelProduct instapay or pesonet and no default rail. It does not post to Titan. "
        + "Integrated mode fails closed when Pulse, Atlas, or Sentinel is unset. "
        + "The standalone stand-in is labeled SIMULATED. "
        + "This operation does not claim settlement, bank_booked, or money_pass.")
    .Produces<PayoutRefusal>(StatusCodes.Status503ServiceUnavailable)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);

// L20: authenticated, tenant-checked, customer in the body; the ledger reference only for an entitled caller.
app.MapPost("/api/remittance/binding", async (
        HttpContext http,
        CustomerRefBody? body,
        ITitanCustomerLedgerBind bind,
        CancellationToken cancellationToken) =>
    {
        var caller = WayfarerCaller.From(http);
        if (caller is null)
            return Results.Unauthorized();
        if (caller.TenantId is null)
            return WayfarerProblems.Forbidden("wayfarer.tenant_missing", "The access token carries no tenant.");

        var scheme = body?.Scheme?.Trim();
        var id = body?.Id?.Trim();
        var tenant = string.IsNullOrWhiteSpace(body?.TenantId) ? caller.TenantId : body!.TenantId!.Trim();
        if (!string.Equals(tenant, caller.TenantId, StringComparison.Ordinal))
            return WayfarerProblems.Forbidden("wayfarer.tenant_mismatch", "tenantId differs from the caller's tenant.");
        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(id))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "scheme and id are required.");

        var customer = await bind.ResolveCustomerAsync(scheme, id, tenant, cancellationToken);
        var ledger = caller.Holds(scheme, id, tenant)
            ? await bind.ResolveLedgerAsync(scheme, id, tenant, body?.CurrencyCode, cancellationToken)
            : TitanCustomerLedgerBind.LedgerWithheld();
        return Results.Ok(new RemittanceBinding(customer, ledger));
    })
    .RequireAuthorization()
    .ExcludeFromDescription();

// X1: the Standalone session routes exist only in Development with Deployment:Mode=Standalone.
if (standaloneAllowed)
{
    app.MapPost("/api/standalone/session", (StandaloneLogin? body, IConfiguration configuration, TimeProvider clock) =>
    {
        if (!StandaloneLocalSession.TryIssue(configuration, body?.OperatorId, body?.TenantId, clock.GetUtcNow(), out var token, out var failure))
            return Results.Json(new { label = StandaloneLocalSession.Label, failure }, statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { token, operatorId = body!.OperatorId!.Trim(), label = StandaloneLocalSession.Label });
    }).AllowAnonymous().ExcludeFromDescription();

    app.MapGet("/api/standalone/session", (HttpContext http) =>
    {
        var caller = WayfarerCaller.From(http);
        return caller is { Standalone: true }
            ? Results.Ok(new { operatorId = caller.Subject, tenantId = caller.TenantId, label = StandaloneLocalSession.Label })
            : Results.Unauthorized();
    }).RequireAuthorization().ExcludeFromDescription();

    app.MapGet("/api/standalone/entitlement", () => Results.Ok(new
    {
        code = StandaloneLocalSession.EntitlementCode,
        label = StandaloneLocalSession.EntitlementLabel,
        allowed = false
    })).AllowAnonymous().ExcludeFromDescription();
}

app.MapGet("/health", () => Results.Ok(RemittanceSystemOfRecord.Health))
    .AllowAnonymous()
    .DisableRateLimiting()
    .WithName("GetHealth")
    .WithTags("Health")
    .WithSummary("Process probe. Live payout is not available.")
    .WithDescription("Reports that the process is up. It does not report a live payout path.")
    .Produces<HostHealth>(StatusCodes.Status200OK);

app.Run();

public sealed record RemittanceBinding(TitanCustomerBindResult Customer, TitanLedgerBindResult Ledger);

public sealed record RemittancePayoutBody(CustomerRefBody? CustomerRef, string? Rail, decimal? AmountPhp);

public sealed record CustomerRefBody(string? Scheme, string? Id, string? TenantId, string? CurrencyCode = null);

public partial class Program;

internal sealed record StandaloneLogin(string? OperatorId, string? TenantId);

internal static class WayfarerProblems
{
    public static IResult Forbidden(string code, string detail) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Forbidden",
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
