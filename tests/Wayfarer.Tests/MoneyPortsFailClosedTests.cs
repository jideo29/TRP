using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Tests;

public sealed class MoneyPortsFailClosedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public MoneyPortsFailClosedTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Posting_status_labels_ports_not_configured_and_keeps_journey_blocked()
    {
        var client = _factory.CreateClient();
        var body = await client.GetFromJsonAsync<JsonElement>("/api/posting/status", Json);

        Assert.Equal(MoneyPortsGate.NotConfigured, body.GetProperty("postingPortStatus").GetString());
        Assert.Equal(MoneyPortsGate.NotConfigured, body.GetProperty("customerDirectoryStatus").GetString());
        Assert.Equal(MoneyPortsGate.NotConfigured, body.GetProperty("documentPortStatus").GetString());
        Assert.False(body.GetProperty("postingPortIsAllow").GetBoolean());
        Assert.False(body.GetProperty("customerDirectoryIsAllow").GetBoolean());
        Assert.False(body.GetProperty("documentPortIsAllow").GetBoolean());
        Assert.False(body.GetProperty("localGlOwned").GetBoolean());
        Assert.False(body.GetProperty("postingSubmitted").GetBoolean());
        Assert.False(body.GetProperty("outboundCall").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.True(body.GetProperty("waveDParked").GetBoolean());
        Assert.Equal("HOLD", body.GetProperty("equicom").GetString());
        Assert.Equal("FROZEN", body.GetProperty("sellOpen").GetString());
        Assert.False(body.GetProperty("adminGlassShipped").GetBoolean());
    }

    [Fact]
    public async Task Posting_submit_unconfigured_is_fail_and_makes_no_outbound_call()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/posting/submit",
            new { instructionId = "remit-1", requestedOutcome = "Allow" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.PostingPortNotConfiguredCode, body.GetProperty("code").GetString());
        Assert.Equal("Fail", body.GetProperty("result").GetString());
        Assert.False(body.GetProperty("isAllowed").GetBoolean());
        Assert.False(body.GetProperty("postingSubmitted").GetBoolean());
        Assert.False(body.GetProperty("outboundCall").GetBoolean());
        Assert.False(body.GetProperty("localGlOwned").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.True(body.GetProperty("waveDParked").GetBoolean());
        var message = body.GetProperty("message").GetString();
        Assert.Contains("PostingPort is NotConfigured", message);
        Assert.Contains("Unknown ≠ Allow", message);
        Assert.Contains("No outbound PostingPort call", message);
    }

    [Fact]
    public async Task Posting_submit_with_dead_base_url_still_fails_closed_without_calling_out()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Ports:Posting:Status", "LabeledOnly");
            builder.UseSetting("Ports:Posting:BaseUrl", "http://127.0.0.1:1");
        });
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/posting/submit",
            new { instructionId = "remit-2", requestedOutcome = "Allow" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.PostingSubmitNotImplementedCode, body.GetProperty("code").GetString());
        Assert.Equal("Fail", body.GetProperty("result").GetString());
        Assert.False(body.GetProperty("isAllowed").GetBoolean());
        Assert.False(body.GetProperty("postingSubmitted").GetBoolean());
        Assert.False(body.GetProperty("outboundCall").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.Contains("No outbound PostingPort call", body.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Stub")]
    [InlineData("Demo")]
    [InlineData("Local")]
    [InlineData("Unknown")]
    public void Posting_stub_local_and_unknown_are_not_allow(string status)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ports:Posting:Status"] = status,
                ["Ports:Posting:BaseUrl"] = "http://127.0.0.1:1",
            })
            .Build();

        var result = MoneyPortsGate.Submit(new PostingSubmitRequest("remit-3", "Allow"), configuration);

        Assert.Equal("Fail", result.Result);
        Assert.False(result.IsAllowed);
        Assert.False(result.PostingSubmitted);
        Assert.False(result.OutboundCall);
        Assert.False(result.MoneyPass);
        Assert.False(result.JourneyAcceptClaimed);
        Assert.Equal("Blocked", result.RemittanceJourney);
        if (status.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(MoneyPortsGate.PostingPortNotConfiguredCode, result.Code);
        }
        else
        {
            Assert.Equal(MoneyPortsGate.StubPostingNotAllowCode, result.Code);
        }
    }

    [Fact]
    public async Task Customer_lookup_unconfigured_is_fail_not_allow()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/customers/lookup",
            new { customerRef = "cif-1" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.CustomerDirectoryNotConfiguredCode, body.GetProperty("code").GetString());
        Assert.Equal("Fail", body.GetProperty("result").GetString());
        Assert.False(body.GetProperty("isAllowed").GetBoolean());
        Assert.False(body.GetProperty("localCifCreated").GetBoolean());
        Assert.False(body.GetProperty("outboundCall").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
        Assert.Contains("Unknown ≠ Allow", body.GetProperty("message").GetString());
    }

    [Fact]
    public void Customer_lookup_labeled_peer_does_not_call_out()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ports:CustomerDirectory:Status"] = "LabeledOnly",
                ["Ports:CustomerDirectory:BaseUrl"] = "http://127.0.0.1:1",
            })
            .Build();

        var result = MoneyPortsGate.LookupCustomer(new CustomerLookupRequest("cif-2"), configuration);

        Assert.Equal(MoneyPortsGate.CustomerDirectoryNotImplementedCode, result.Code);
        Assert.Equal("Fail", result.Result);
        Assert.False(result.IsAllowed);
        Assert.False(result.LocalCifCreated);
        Assert.False(result.OutboundCall);
        Assert.False(result.MoneyPass);
    }

    [Fact]
    public async Task Local_cif_create_is_refused()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/customers", new { customerRef = "local-1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.LocalCifRefusedCode, body.GetProperty("code").GetString());
        Assert.False(body.GetProperty("localCifCreated").GetBoolean());
        Assert.False(body.GetProperty("isAllowed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
    }

    [Theory]
    [InlineData("/api/ledger/balances")]
    [InlineData("/api/balances")]
    public async Task Local_gl_mutation_is_refused(string path)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(path, new { amount = "100.00", currency = "PHP" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.LocalGlRefusedCode, body.GetProperty("code").GetString());
        Assert.Equal("Fail", body.GetProperty("result").GetString());
        Assert.False(body.GetProperty("localGlOwned").GetBoolean());
        Assert.False(body.GetProperty("balanceChanged").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.False(body.GetProperty("journeyAcceptClaimed").GetBoolean());
        Assert.Equal("Blocked", body.GetProperty("remittanceJourney").GetString());
    }

    [Fact]
    public async Task Document_evidence_unconfigured_is_fail_not_allow()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/documents/evidence", new { pack = "remittance-receipt" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(MoneyPortsGate.DocumentPortNotConfiguredCode, body.GetProperty("code").GetString());
        Assert.Equal("Fail", body.GetProperty("result").GetString());
        Assert.False(body.GetProperty("isAllowed").GetBoolean());
        Assert.False(body.GetProperty("evidenceWritten").GetBoolean());
        Assert.False(body.GetProperty("outboundCall").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());
        Assert.Contains("Unknown ≠ Allow", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Host_and_health_keep_money_pass_false_and_admin_glass_unshipped()
    {
        var client = _factory.CreateClient();

        var host = await client.GetFromJsonAsync<JsonElement>("/api/host", Json);
        Assert.False(host.GetProperty("moneyPass").GetBoolean());
        Assert.Equal("Blocked", host.GetProperty("remittanceJourney").GetString());
        Assert.True(host.GetProperty("waveDParked").GetBoolean());
        Assert.False(host.GetProperty("adminGlassShipped").GetBoolean());
        Assert.False(host.GetProperty("moneyPorts").GetProperty("postingPortIsAllow").GetBoolean());
        Assert.False(host.GetProperty("moneyPorts").GetProperty("moneyPass").GetBoolean());

        var health = await client.GetFromJsonAsync<JsonElement>("/health", Json);
        Assert.False(health.GetProperty("moneyPass").GetBoolean());
        Assert.Equal(MoneyPortsGate.NotConfigured, health.GetProperty("postingPortStatus").GetString());
        Assert.Equal(MoneyPortsGate.NotConfigured, health.GetProperty("customerDirectoryStatus").GetString());
        Assert.False(health.GetProperty("localGlOwned").GetBoolean());
        Assert.False(health.GetProperty("postingSubmitted").GetBoolean());
        Assert.False(health.GetProperty("outboundCall").GetBoolean());
        Assert.False(health.GetProperty("adminGlassShipped").GetBoolean());
        Assert.False(health.GetProperty("journeyAcceptClaimed").GetBoolean());
    }

    [Fact]
    public void Manifest_records_posting_and_customer_ports_as_not_configured()
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "manifest.json"));
        using var doc = JsonDocument.Parse(raw);
        var honesty = doc.RootElement.GetProperty("honesty");

        Assert.Equal("NotConfigured", honesty.GetProperty("postingPortStatus").GetString());
        Assert.Equal("NotConfigured", honesty.GetProperty("customerDirectoryStatus").GetString());
        Assert.Equal("NotConfigured", honesty.GetProperty("documentPortStatus").GetString());
        Assert.False(honesty.GetProperty("localGlOwned").GetBoolean());
        Assert.False(honesty.GetProperty("postingSubmitted").GetBoolean());
        Assert.False(honesty.GetProperty("moneyPass").GetBoolean());
        Assert.False(honesty.GetProperty("adminGlassShipped").GetBoolean());
        Assert.Equal("Blocked", honesty.GetProperty("remittanceJourney").GetString());
        Assert.True(doc.RootElement.GetProperty("waveDParked").GetBoolean());
    }

    [Theory]
    [InlineData("Wayfarer:ClaimMoneyPass", "money_pass invent is refused")]
    [InlineData("Wayfarer:OwnLocalGl", "local GL ownership is refused")]
    [InlineData("Wayfarer:AllowStubPosting", "stub posting is not Allow")]
    public void Money_port_invent_flags_refuse_to_boot(string key, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Deployment:Mode"] = "Standalone",
                [key] = "true",
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() => FailClosedGate.EnsureCanBoot(configuration));
        Assert.Contains(expected, ex.Message);
        Assert.Contains(RemittanceSystemOfRecord.Statement, ex.Message);
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
