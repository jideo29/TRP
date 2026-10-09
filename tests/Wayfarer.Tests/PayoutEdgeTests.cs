using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Tests;

public sealed class PayoutEdgeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Standalone_stand_in_is_labeled_simulated_and_claims_nothing()
    {
        var calls = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var payout = new RemittancePayout(Config("Standalone", "", "", ""), calls);
        var result = await payout.ExecuteAsync(Input(), CancellationToken.None);

        Assert.Equal(0, calls.Count);
        Assert.Equal(RemittancePayout.SimulatedLabel, result.Label);
        Assert.Contains("SIMULATED", result.Message, StringComparison.Ordinal);
        Assert.False(result.PayoutExecuted);
        Assert.False(result.MoneyPass);
        Assert.False(result.BankBooked);
        Assert.False(result.SettlementClaimed);
        Assert.Equal(503, result.StatusCode);
    }

    [Fact]
    public async Task Integrated_mode_fails_closed_when_peers_are_unset()
    {
        var calls = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var payout = new RemittancePayout(Config("PlatformIntegrated", "", "", ""), calls);
        var result = await payout.ExecuteAsync(Input(), CancellationToken.None);

        Assert.Equal(0, calls.Count);
        Assert.Equal(RemittancePayout.NotConfiguredLabel, result.Label);
        Assert.Equal("REMITTANCE_NOT_CONFIGURED", result.Code);
        Assert.False(result.PayoutExecuted);
        Assert.False(result.MoneyPass);
        Assert.False(result.BankBooked);
        Assert.False(result.SettlementClaimed);
    }

    [Fact]
    public async Task Missing_customerRef_stays_fail_closed()
    {
        var payout = new RemittancePayout(Config("Standalone", "", "", ""));
        var result = await payout.ExecuteAsync(
            new RemittancePayoutInput(null, null, null, "instapay", 10m, "token", "corr"),
            CancellationToken.None);

        Assert.Equal(RemittanceSystemOfRecord.FailClosedCode, result.Code);
        Assert.False(result.PayoutExecuted);
        Assert.False(result.MoneyPass);
        Assert.False(result.BankBooked);
        Assert.False(result.SettlementClaimed);
    }

    [Fact]
    public async Task Configured_payout_consults_atlas_and_sentinel_then_posts_toward_pulse()
    {
        var calls = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("consultation", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """{"isAllowed":true,"decision":"Allow"}""");
            }

            if (path.Contains("risk/decisions", StringComparison.Ordinal))
            {
                return JsonResponse(HttpStatusCode.OK, """{"outcome":"Allow"}""");
            }

            return JsonResponse(HttpStatusCode.OK, """{"transferId":"pulse-1"}""");
        });

        var payout = new RemittancePayout(
            Config("PlatformIntegrated", "http://pulse.example", "http://atlas.example", "http://sentinel.example", "Development"),
            calls);
        var result = await payout.ExecuteAsync(Input(rail: "instapay", amount: 25m, bearer: "token-1"), CancellationToken.None);

        Assert.Equal(RemittancePayout.TowardPulseLabel, result.Label);
        Assert.False(result.PayoutExecuted);
        Assert.False(result.MoneyPass);
        Assert.False(result.BankBooked);
        Assert.False(result.SettlementClaimed);
        Assert.Equal(3, calls.Count);
        Assert.Contains("/v1/consultation", calls.Paths[0], StringComparison.Ordinal);
        Assert.Contains("platform=wayfarer", calls.Paths[0], StringComparison.Ordinal);
        Assert.Equal("Bearer token-1", calls.Authorizations[0]);
        Assert.Contains("/api/v1/risk/decisions", calls.Paths[1], StringComparison.Ordinal);
        Assert.DoesNotContain("score", calls.Bodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/instapay/transfers", calls.Paths[2], StringComparison.Ordinal);
        Assert.DoesNotContain("posting", calls.Paths[2], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("titan", calls.Paths[2], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("score", calls.Bodies[2], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bank_booked", calls.Bodies[2], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money_pass", calls.Bodies[2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Atlas_unknown_and_a_titan_rail_fail_closed_before_pulse()
    {
        var calls = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, """{"isAllowed":false,"decision":"Unknown"}"""));
        var payout = new RemittancePayout(
            Config("PlatformIntegrated", "http://pulse.example", "http://atlas.example", "http://sentinel.example", "Development"),
            calls);
        var unknown = await payout.ExecuteAsync(Input(bearer: "token-1"), CancellationToken.None);
        Assert.Equal(RemittancePayout.RefusedLabel, unknown.Label);
        Assert.False(unknown.SettlementClaimed);
        Assert.Single(calls.Paths);
        Assert.Contains("/v1/consultation", calls.Paths[0], StringComparison.Ordinal);

        calls = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, """{"isAllowed":true,"decision":"Allow","outcome":"Allow"}"""));
        payout = new RemittancePayout(
            Config("Standalone", "http://pulse.example", "http://atlas.example", "http://sentinel.example", "Staging"),
            calls);
        var titanRail = await payout.ExecuteAsync(Input(rail: "titan", amount: 10m, bearer: "token-1"), CancellationToken.None);
        Assert.Equal(RemittancePayout.RefusedLabel, titanRail.Label);
        Assert.DoesNotContain(calls.Paths, path => path.Contains("posting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(calls.Paths, path => path.Contains("instapay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sentinel_block_does_not_call_pulse()
    {
        var calls = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("consultation", StringComparison.Ordinal))
                return JsonResponse(HttpStatusCode.OK, """{"isAllowed":true,"decision":"Allow"}""");
            return JsonResponse(HttpStatusCode.OK, """{"outcome":"Block","score":90}""");
        });
        var payout = new RemittancePayout(
            Config("PlatformIntegrated", "http://pulse.example", "http://atlas.example", "http://sentinel.example", "Production"),
            calls);
        var result = await payout.ExecuteAsync(Input(rail: "pesonet", amount: 10m, bearer: "token-1"), CancellationToken.None);

        Assert.Equal(RemittancePayout.RefusedLabel, result.Label);
        Assert.False(result.MoneyPass);
        Assert.Equal(2, calls.Count);
        Assert.DoesNotContain(calls.Paths, path => path.Contains("pesonet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Host_standalone_labels_the_stand_in_and_still_refuses_a_missing_customerRef()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
        var missing = await client.PostAsync("/api/remittance/payout", empty);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
        var missingBody = await missing.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittanceSystemOfRecord.FailClosedCode, missingBody.GetProperty("code").GetString());
        Assert.False(missingBody.GetProperty("moneyPass").GetBoolean());
        Assert.False(missingBody.GetProperty("bankBooked").GetBoolean());
        Assert.False(missingBody.GetProperty("settlementClaimed").GetBoolean());

        var payload = JsonSerializer.Serialize(new
        {
            customerRef = new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" }
        }, Json);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var simulated = await client.PostAsync("/api/remittance/payout", content);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, simulated.StatusCode);
        var body = await simulated.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittancePayout.SimulatedLabel, body.GetProperty("label").GetString());
        Assert.Contains("SIMULATED", body.GetProperty("message").GetString());
        Assert.False(body.GetProperty("payoutExecuted").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("bankBooked").GetBoolean());
        Assert.False(body.GetProperty("settlementClaimed").GetBoolean());
    }

    private static RemittancePayoutInput Input(string? rail = null, decimal? amount = null, string? bearer = null) =>
        new("titan-cif", "cif-1", "tenant-1", rail, amount, bearer, "corr-1");

    private static IConfiguration Config(
        string mode,
        string pulse,
        string atlas,
        string sentinel,
        string? environment = "") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = mode,
            ["Pulse:BaseUrl"] = pulse,
            ["Atlas:BaseUrl"] = atlas,
            ["Atlas:Environment"] = environment,
            ["Sentinel:BaseUrl"] = sentinel,
            ["Wayfarer:LivePayout"] = "false"
        }).Build();

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public int Count => Paths.Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri?.PathAndQuery ?? "");
            Authorizations.Add(request.Headers.Authorization?.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
