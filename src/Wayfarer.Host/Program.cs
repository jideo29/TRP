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

app.MapGet("/health", () => Results.Ok(RemittanceSystemOfRecord.Health))
    .WithName("GetHealth")
    .WithTags("Health")
    .WithSummary("Process probe. Live payout is not available.")
    .WithDescription("Reports that the process is up. It does not report a live payout path.")
    .Produces<HostHealth>(StatusCodes.Status200OK);

app.Run();

public partial class Program;
