using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Tests;

public sealed class RemittanceHostTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public RemittanceHostTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Host_states_it_is_the_remittance_system_of_record_not_pulse()
    {
        var client = _factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/host", Json);

        Assert.Equal("Wayfarer", body.GetProperty("product").GetString());
        Assert.Equal("jideo29/TRP", body.GetProperty("repository").GetString());
        Assert.Equal("remittance system of record", body.GetProperty("role").GetString());
        Assert.True(body.GetProperty("notPulse").GetBoolean());
        Assert.Equal("T-11", body.GetProperty("tension").GetString());
        Assert.Equal(RemittanceSystemOfRecord.Statement, body.GetProperty("statement").GetString());
        Assert.False(body.GetProperty("livePayout").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.False(body.GetProperty("remittanceJourneyUnblocked").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.False(body.GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.False(body.GetProperty("novaRemittanceIntentsAbsorbedByPulse").GetBoolean());
        Assert.True(body.GetProperty("waveDParked").GetBoolean());
        Assert.Equal("HOLD", body.GetProperty("equicom").GetString());
        Assert.False(body.GetProperty("outboundEmail").GetBoolean());
        Assert.Equal("FROZEN", body.GetProperty("sellOpen").GetString());

        var rails = body.GetProperty("forbiddenPulseRails").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        Assert.Contains("INSTAPAY", rails);
        Assert.Contains("PESONET", rails);
        Assert.Contains("COLLECTIONS", rails);
    }

    [Fact]
    public async Task Manifest_declares_remittance_sor_and_refuses_pulse_absorption()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/manifest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("wayfarer", body.GetProperty("systemOfRecord").GetProperty("remittancePayout").GetString());
        Assert.Equal("pulse", body.GetProperty("systemOfRecord").GetProperty("paymentRails").GetString());
        Assert.False(body.GetProperty("honesty").GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.False(body.GetProperty("honesty").GetProperty("novaRemittanceIntentsAbsorbedByPulse").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("honesty").GetProperty("remittanceJourney").GetString());
        Assert.False(body.GetProperty("honesty").GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("honesty").GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.True(body.GetProperty("waveDParked").GetBoolean());
        Assert.Equal("T-11", body.GetProperty("honesty").GetProperty("tension").GetString());
    }

    [Fact]
    public async Task Payout_fails_closed_and_does_not_execute()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/remittance/payout", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittanceSystemOfRecord.FailClosedCode, body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("payoutExecuted").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        var message = body.GetProperty("message").GetString();
        Assert.Contains("Live payout is not implemented.", message);
        Assert.Contains(RemittanceSystemOfRecord.Statement, message);
        Assert.Contains("The remittance journey is not unblocked.", message);
    }

    [Theory]
    [InlineData("INSTAPAY")]
    [InlineData("PESONET")]
    [InlineData("PDDTS")]
    [InlineData("SWIFT")]
    [InlineData("BILLS")]
    [InlineData("QR")]
    [InlineData("ECOMMERCE")]
    [InlineData("COLLECTIONS")]
    [InlineData("instapay")]
    public async Task Payout_refuses_pulse_rail_absorption_t11(string rail)
    {
        var client = _factory.CreateClient();
        using var content = new StringContent(
            JsonSerializer.Serialize(new { rail }),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/remittance/payout", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittanceSystemOfRecord.PulseRailAbsorptionCode, body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("payoutExecuted").GetBoolean());
        Assert.False(body.GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.Contains("Pulse rail absorption is refused (T-11)", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Nova_remittance_intent_intake_fails_closed_and_is_not_absorbed_by_pulse()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent(
            """{"intentId":"nova-remit-1","corridor":"PH-US","amount":100}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/remittance/intents", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittanceSystemOfRecord.NovaIntentIntakeCode, body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("intentAcceptedForProcessing").GetBoolean());
        Assert.False(body.GetProperty("payoutExecuted").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.False(body.GetProperty("absorbedByPulse").GetBoolean());
        Assert.Equal("wayfarer", body.GetProperty("owner").GetString());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.Contains("Nova remittance intents must come to Wayfarer, not Pulse (T-11)", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Nova_intent_with_pulse_rail_is_refused_as_t11_absorption()
    {
        var client = _factory.CreateClient();
        using var content = new StringContent(
            """{"intentId":"bad","rail":"INSTAPAY","amount":50}""",
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync("/api/remittance/intents", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(RemittanceSystemOfRecord.PulseRailAbsorptionCode, body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("absorbedByPulse").GetBoolean());
        Assert.False(body.GetProperty("intentAcceptedForProcessing").GetBoolean());
    }

    [Fact]
    public async Task Health_keeps_journey_blocked_and_money_pass_false()
    {
        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/health", Json);

        Assert.Equal("process-up", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.False(body.GetProperty("remittanceJourneyUnblocked").GetBoolean());
        Assert.False(body.GetProperty("livePayout").GetBoolean());
        Assert.False(body.GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.True(body.GetProperty("waveDParked").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
    }

    [Fact]
    public void Live_payout_configuration_fails_closed_at_startup()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Wayfarer:LivePayout", "true");
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("fails closed", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live payout is not implemented", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FailClosedGateTests
{
    [Theory]
    [InlineData("TrudiIntegrated")]
    [InlineData("PlatformIntegrated")]
    [InlineData("ForeignIntegrated")]
    [InlineData("Production")]
    public void Regulated_mode_refuses_to_boot(string mode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = mode,
                ["Wayfarer:LivePayout"] = "false",
                ["Ports:Posting:BaseUrl"] = "http://titan.example",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FailClosedGate.EnsureCanBoot(configuration));
        Assert.Contains("fails closed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(RemittanceSystemOfRecord.Statement, ex.Message);
        Assert.Contains("The remittance journey is not unblocked.", ex.Message);
    }

    [Fact]
    public void Absorb_pulse_rails_configuration_refuses_to_boot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "Standalone",
                ["Wayfarer:AbsorbPulseRails"] = "true",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FailClosedGate.EnsureCanBoot(configuration));
        Assert.Contains("Pulse rail absorption is refused (T-11)", ex.Message);
    }

    [Fact]
    public void Soft_unpark_configuration_refuses_to_boot()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "Standalone",
                ["Wayfarer:RemittanceJourneyUnblocked"] = "true",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FailClosedGate.EnsureCanBoot(configuration));
        Assert.Contains("soft-unpark is refused", ex.Message);
        Assert.Contains("Wave D stays PARKED", ex.Message);
    }

    [Fact]
    public void Standalone_skeleton_may_boot_and_payout_still_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "Standalone",
                ["Wayfarer:LivePayout"] = "false",
                ["Wayfarer:AbsorbPulseRails"] = "false",
                ["Wayfarer:RemittanceJourneyUnblocked"] = "false",
            })
            .Build();

        FailClosedGate.EnsureCanBoot(configuration);

        var refusal = RemittanceSystemOfRecord.RefusePayout();
        Assert.Equal(503, refusal.StatusCode);
        Assert.False(refusal.PayoutExecuted);
        Assert.False(refusal.MoneyPass);
        Assert.False(refusal.PulseRailsAbsorbed);
        Assert.Equal("Blocked", refusal.RemittanceJourney);
        Assert.False(RemittanceSystemOfRecord.Identity.MoneyPass);
        Assert.False(RemittanceSystemOfRecord.Identity.RemittanceJourneyUnblocked);
        Assert.False(RemittanceSystemOfRecord.Identity.LivePayout);
        Assert.False(RemittanceSystemOfRecord.Identity.PulseRailsAbsorbed);
        Assert.True(RemittanceSystemOfRecord.Identity.NotPulse);
        Assert.True(RemittanceSystemOfRecord.Identity.WaveDParked);
    }
}

public sealed class ReadmeAndDoctrineTests
{
    [Fact]
    public void Readme_directs_nova_remittance_intents_here_and_keeps_the_journey_blocked()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));

        Assert.Contains(
            "Nova remittance intents must come here later, not be absorbed by Pulse.",
            readme);
        Assert.Contains("Live payout is not implemented.", readme);
        Assert.Contains("The remittance journey is not unblocked.", readme);
        Assert.Contains("T11_PULSE_RAIL_ABSORPTION_REFUSED", readme);
        Assert.Contains("HOLD", readme);
        Assert.Contains("FROZEN", readme);
        Assert.Contains("PARKED", readme);
        Assert.DoesNotContain("money_pass=true", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money_pass: true", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journey is unblocked", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T11_doctrine_file_states_pulse_rails_are_not_absorbed()
    {
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "architecture", "t-11-not-pulse-rails.md"));

        Assert.Contains("T-11", doc);
        Assert.Contains("Pulse rail absorption", doc);
        Assert.Contains("pulseRailsAbsorbed=false", doc);
        Assert.Contains("Journey Accept is **not** claimed", doc);
        Assert.Contains("PARKED", doc);
        Assert.DoesNotContain("Journey Accepted", doc);
    }

    [Fact]
    public void Manifest_on_disk_matches_t11_honesty()
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "manifest.json"));
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        Assert.Equal("wayfarer", root.GetProperty("systemOfRecord").GetProperty("remittancePayout").GetString());
        Assert.False(root.GetProperty("honesty").GetProperty("pulseRailsAbsorbed").GetBoolean());
        Assert.Equal("Blocked", root.GetProperty("honesty").GetProperty("remittanceJourney").GetString());
        Assert.False(root.GetProperty("honesty").GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.True(root.GetProperty("waveDParked").GetBoolean());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wayfarer.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Wayfarer.sln from the test output directory.");
    }
}
