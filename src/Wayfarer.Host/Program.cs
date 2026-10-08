using Wayfarer;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);
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

app.MapPost("/api/remittance/payout", () =>
{
    var refusal = RemittanceSystemOfRecord.RefusePayout();
    return Results.Json(refusal, statusCode: refusal.StatusCode);
})
    .WithName("RefusePayout")
    .WithTags("Remittance")
    .WithSummary("Refuses a payout. No money movement is executed.")
    .WithDescription("Live payout is not implemented. This operation always fails closed.")
    .Produces<PayoutRefusal>(StatusCodes.Status503ServiceUnavailable);

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

public partial class Program;

internal sealed record StandaloneLogin(string? OperatorId);
