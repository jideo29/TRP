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
        Assert.False(body.GetProperty("remittanceJourneyUnblocked").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.False(body.GetProperty("novaRemittanceIntentsAbsorbedByPulse").GetBoolean());
        Assert.Equal("HOLD", body.GetProperty("equicom").GetString());
        Assert.False(body.GetProperty("outboundEmail").GetBoolean());
        Assert.Equal("FROZEN", body.GetProperty("sellOpen").GetString());
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
        var message = body.GetProperty("message").GetString();
        Assert.Contains("Live payout is not implemented.", message);
        Assert.Contains(RemittanceSystemOfRecord.Statement, message);
        Assert.Contains("The remittance journey is not unblocked.", message);
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
    public void Standalone_skeleton_may_boot_and_payout_still_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "Standalone",
                ["Wayfarer:LivePayout"] = "false",
            })
            .Build();

        FailClosedGate.EnsureCanBoot(configuration);

        var refusal = RemittanceSystemOfRecord.RefusePayout();
        Assert.Equal(503, refusal.StatusCode);
        Assert.False(refusal.PayoutExecuted);
        Assert.False(refusal.MoneyPass);
        Assert.False(RemittanceSystemOfRecord.Identity.MoneyPass);
        Assert.False(RemittanceSystemOfRecord.Identity.RemittanceJourneyUnblocked);
        Assert.False(RemittanceSystemOfRecord.Identity.LivePayout);
        Assert.True(RemittanceSystemOfRecord.Identity.NotPulse);
    }
}

public sealed class ReadmeTests
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
        Assert.Contains("HOLD", readme);
        Assert.Contains("FROZEN", readme);
        Assert.DoesNotContain("money_pass=true", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money_pass: true", readme, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journey is unblocked", readme, StringComparison.OrdinalIgnoreCase);
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
