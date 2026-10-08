using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Tests;

public sealed class TitanCustomerLedgerBindTests
{
    [Fact]
    public async Task Integrated_logic_fails_closed_when_titan_is_unset_or_refuses()
    {
        var missing = new TitanCustomerLedgerBind(Config("PlatformIntegrated", "", ""));
        var customer = await missing.ResolveCustomerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None);
        var ledger = await missing.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None);
        Assert.Equal(TitanBindOutcome.NotConfigured, customer.Outcome);
        Assert.Equal(TitanBindOutcome.NotConfigured, ledger.Outcome);
        Assert.False(ledger.LedgerRowWritten);

        var handler = new ScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var refused = new TitanCustomerLedgerBind(Config("PlatformIntegrated", "http://127.0.0.1:9", "http://127.0.0.1:9"), handler);
        Assert.Equal(TitanBindOutcome.Refused, (await refused.ResolveCustomerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None)).Outcome);
        Assert.False((await refused.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None)).LedgerRowWritten);
    }

    [Fact]
    public async Task Standalone_host_returns_labeled_stand_ins_and_does_not_post()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Standalone");
            builder.UseSetting("Wayfarer:LivePayout", "false");
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/remittance/binding?scheme=titan-cif&id=cif-1&tenantId=tenant-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RemittanceBinding>();
        Assert.NotNull(body);
        Assert.Equal(TitanCustomerLedgerBind.CustomerStandInLabel, body!.Customer.Label);
        Assert.Equal(TitanCustomerLedgerBind.LedgerStandInLabel, body.Ledger.Label);
        Assert.False(body.Ledger.LedgerRowWritten);
    }

    private static IConfiguration Config(string mode, string customerUrl, string ledgerUrl) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = mode,
            ["Titan:CustomerDirectory:BaseUrl"] = customerUrl,
            ["Titan:Ledger:BaseUrl"] = ledgerUrl,
        }).Build();

    private sealed class ScriptHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
