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

        var noCredential = new TitanCustomerLedgerBind(Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"));
        var noToken = await noCredential.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None);
        Assert.Equal(TitanBindOutcome.NotConfigured, noToken.Outcome);
        Assert.Equal("titan.ledger.credential_missing", noToken.Code);

        var handler = new ScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var refused = new TitanCustomerLedgerBind(
            Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"), handler, new FixedToken("titan-service-token"));
        Assert.Equal(TitanBindOutcome.Refused, (await refused.ResolveCustomerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None)).Outcome);
        Assert.False((await refused.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None)).LedgerRowWritten);
    }

    [Fact]
    public async Task Titan_is_called_with_the_wayfarer_client_credential_token()
    {
        var authorizations = new List<string?>();
        var handler = new ScriptHandler(request =>
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ledgerAccountRef":"ledger-synthetic-1"}""")
            };
        });
        var bind = new TitanCustomerLedgerBind(
            Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"), handler, new FixedToken("titan-service-token"));

        var ledger = await bind.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None);

        Assert.Equal(TitanBindOutcome.Bound, ledger.Outcome);
        Assert.Equal(["Bearer titan-service-token"], authorizations);
    }

    [Fact]
    public async Task Standalone_host_returns_labeled_stand_ins_and_does_not_post()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Deployment:Mode", "Standalone");
            builder.UseSetting("Wayfarer:LivePayout", "false");
            builder.UseSetting("Standalone:SigningKey", "wayfarer-standalone-test-key");
        });
        using var client = factory.CreateClient();

        // L20: the query-string GET is gone and the route needs a caller.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/remittance/binding?scheme=titan-cif&id=cif-1&tenantId=tenant-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync("/api/remittance/binding", new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" })).StatusCode);

        using var session = await client.PostAsJsonAsync("/api/standalone/session", new { operatorId = "wayfarer-operator", tenantId = "tenant-1" });
        var token = (await session.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/remittance/binding", new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<RemittanceBinding>();
        Assert.NotNull(body);
        Assert.Equal(TitanCustomerLedgerBind.CustomerStandInLabel, body!.Customer.Label);
        // The session names no customer, so the ledger reference is withheld.
        Assert.Equal("titan.ledger.not_entitled", body.Ledger.Code);
        Assert.Null(body.Ledger.LedgerAccountRef);
        Assert.False(body.Ledger.LedgerRowWritten);

        var otherTenant = await client.PostAsJsonAsync("/api/remittance/binding", new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-2" });
        Assert.Equal(HttpStatusCode.Forbidden, otherTenant.StatusCode);
    }

    private static IConfiguration Config(string mode, string customerUrl, string ledgerUrl) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = mode,
            ["Titan:CustomerDirectory:BaseUrl"] = customerUrl,
            ["Titan:Ledger:BaseUrl"] = ledgerUrl,
        }).Build();

    private sealed class FixedToken(string token) : Wayfarer.Security.ITitanAccessTokenSource
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(token);
    }

    private sealed class ScriptHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
