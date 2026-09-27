using System.Text.Json;
using Wayfarer;

var builder = WebApplication.CreateBuilder(args);
FailClosedGate.EnsureCanBoot(builder.Configuration);

var app = builder.Build();

app.MapGet("/api/host", () => Results.Ok(RemittanceSystemOfRecord.Identity));

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

    var refusal = RemittanceSystemOfRecord.RefusePayout(body?.Rail ?? body?.Channel);
    return Results.Json(refusal, statusCode: refusal.StatusCode);
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
