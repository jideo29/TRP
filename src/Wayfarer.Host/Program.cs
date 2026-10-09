using Wayfarer;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);
builder.Services.AddSingleton<ITitanCustomerLedgerBind>(_ => new TitanCustomerLedgerBind(builder.Configuration));
builder.Services.AddSingleton(_ => new RemittancePayout(builder.Configuration));
builder.AddWayfarerOpenApi();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWayfarerOpenApi();

app.MapGet("/api/host", () => Results.Ok(RemittanceSystemOfRecord.Identity))
    .WithName("GetHostIdentity")
    .WithTags("Host")
    .WithSummary("Returns the identity of this fail-closed remittance host.")
    .WithDescription("Reads the published identity of this process. It does not enable payout.")
    .Produces<HostIdentity>(StatusCodes.Status200OK);

app.MapPost("/api/remittance/payout", async (
        HttpRequest request,
        RemittancePayout payout,
        CancellationToken cancellationToken) =>
{
    var body = await RemittancePayout.ReadBodyAsync(request, cancellationToken);
    var header = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    var bearer = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : string.Empty;
    var result = await payout.ExecuteAsync(
        new RemittancePayoutInput(
            body?.CustomerRef?.Scheme,
            body?.CustomerRef?.Id,
            body?.CustomerRef?.TenantId,
            body?.Rail,
            body?.AmountPhp,
            bearer,
            request.Headers["X-Correlation-Id"].ToString()),
        cancellationToken);
    return Results.Json(result, statusCode: result.StatusCode);
})
    .WithName("RefusePayout")
    .WithTags("Remittance")
    .WithSummary("Payout toward Pulse. No settlement is claimed.")
    .WithDescription(
        "Live payout is not implemented. customerRef is required. "
        + "The handoff is POST /corp-pay/orchestrate, the Pulse path Summit uses, with channelProduct instapay or pesonet and no default rail. It does not post to Titan. "
        + "Integrated mode fails closed when Pulse, Atlas, or Sentinel is unset. "
        + "The standalone stand-in is labeled SIMULATED. "
        + "This operation does not claim settlement, bank_booked, or money_pass.")
    .Produces<PayoutRefusal>(StatusCodes.Status503ServiceUnavailable);

app.MapGet("/api/remittance/binding", async (
        string scheme,
        string id,
        string tenantId,
        ITitanCustomerLedgerBind bind,
        CancellationToken cancellationToken) =>
    {
        var customer = await bind.ResolveCustomerAsync(scheme, id, tenantId, cancellationToken);
        var ledger = await bind.ResolveLedgerAsync(scheme, id, tenantId, cancellationToken);
        return Results.Ok(new RemittanceBinding(customer, ledger));
    })
    .ExcludeFromDescription();

app.MapPost("/api/standalone/session", (StandaloneLogin? body, IConfiguration configuration) =>
{
    if (!StandaloneLocalSession.TryIssue(configuration, body?.OperatorId, DateTimeOffset.UtcNow, out var token, out var failure))
        return Results.Json(new { label = StandaloneLocalSession.Label, failure }, statusCode: StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(new { token, operatorId = body!.OperatorId!.Trim(), label = StandaloneLocalSession.Label });
}).ExcludeFromDescription();

app.MapGet("/api/standalone/session", (HttpRequest request, IConfiguration configuration) =>
{
    var header = request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    var presented = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..] : string.Empty;
    return StandaloneLocalSession.TryRead(configuration, presented, out var operatorId)
        ? Results.Ok(new { operatorId, label = StandaloneLocalSession.Label })
        : Results.Unauthorized();
}).ExcludeFromDescription();

app.MapGet("/api/standalone/entitlement", () => Results.Ok(new
{
    code = StandaloneLocalSession.EntitlementCode,
    label = StandaloneLocalSession.EntitlementLabel,
    allowed = false
})).ExcludeFromDescription();

app.MapGet("/health", () => Results.Ok(RemittanceSystemOfRecord.Health))
    .WithName("GetHealth")
    .WithTags("Health")
    .WithSummary("Process probe. Live payout is not available.")
    .WithDescription("Reports that the process is up. It does not report a live payout path.")
    .Produces<HostHealth>(StatusCodes.Status200OK);

app.Run();

public sealed record RemittanceBinding(TitanCustomerBindResult Customer, TitanLedgerBindResult Ledger);

public sealed record RemittancePayoutBody(CustomerRefBody? CustomerRef, string? Rail, decimal? AmountPhp);

public sealed record CustomerRefBody(string? Scheme, string? Id, string? TenantId);

public partial class Program;

internal sealed record StandaloneLogin(string? OperatorId);
