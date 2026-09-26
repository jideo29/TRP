using Wayfarer;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);

var app = builder.Build();

app.MapGet("/api/host", () => Results.Ok(RemittanceSystemOfRecord.Identity));

app.MapPost("/api/remittance/payout", () =>
{
    var refusal = RemittanceSystemOfRecord.RefusePayout();
    return Results.Json(refusal, statusCode: refusal.StatusCode);
});

app.MapGet("/health", () => Results.Ok(new
{
    status = "process-up",
    moneyPass = false,
    remittanceJourneyUnblocked = false,
    livePayout = false,
}));

app.Run();

public partial class Program;
