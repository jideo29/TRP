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
        var ledger = await missing.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", null, CancellationToken.None);
        Assert.Equal(TitanBindOutcome.NotConfigured, customer.Outcome);
        Assert.Equal(TitanBindOutcome.NotConfigured, ledger.Outcome);
        Assert.False(ledger.LedgerRowWritten);

        var noCredential = new TitanCustomerLedgerBind(Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"));
        var noToken = await noCredential.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", null, CancellationToken.None);
        Assert.Equal(TitanBindOutcome.NotConfigured, noToken.Outcome);
        Assert.Equal("titan.ledger.credential_missing", noToken.Code);

        var handler = new ScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var refused = new TitanCustomerLedgerBind(
            Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"), handler, new FixedToken("titan-service-token"));
        Assert.Equal(TitanBindOutcome.Refused, (await refused.ResolveCustomerAsync("titan-cif", "cif-1", "tenant-1", CancellationToken.None)).Outcome);
        Assert.False((await refused.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", null, CancellationToken.None)).LedgerRowWritten);
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
                Content = new StringContent("""{"data":{"scheme":"titan-cif","id":"cif-1","tenantId":"tenant-1","ledgerAccountRef":"ledger-synthetic-1","currencyCode":"PHP"}}""")
            };
        });
        var bind = new TitanCustomerLedgerBind(
            Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"), handler, new FixedToken("titan-service-token"));

        var ledger = await bind.ResolveLedgerAsync("titan-cif", "cif-1", "tenant-1", null, CancellationToken.None);

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

    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Customer_lookup_posts_the_ref_in_the_body_and_binds_on_200()
    {
        var calls = new List<(HttpMethod Method, string Url, string Body)>();
        var bind = Bind(Recording(calls, HttpStatusCode.OK,
            $$$"""{"data":{"scheme":"titan-cif","id":"CIF-001","tenantId":"{{{Tenant}}}","displayHint":"Synthetic","synthetic":true}}"""));

        var result = await bind.ResolveCustomerAsync("titan-cif", "CIF-001", Tenant, CancellationToken.None);

        Assert.Equal(TitanBindOutcome.Bound, result.Outcome);
        Assert.Equal(("titan-cif", "CIF-001", Tenant), (result.Scheme, result.Id, result.TenantId));
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://titan.synthetic.test/api/customer-directory/refs/lookup", call.Url);
        AssertBody(call.Body, "titan-cif", "CIF-001", Tenant, currency: null);
    }

    [Fact]
    public async Task Ledger_lookup_posts_the_ref_and_currency_in_the_body_and_binds_on_200()
    {
        var calls = new List<(HttpMethod Method, string Url, string Body)>();
        var bind = Bind(Recording(calls, HttpStatusCode.OK,
            $$$"""{"data":{"scheme":"titan-cif","id":"CIF-001","tenantId":"{{{Tenant}}}","ledgerAccountRef":"ACC-SYN-1","currencyCode":"PHP"}}"""));

        var result = await bind.ResolveLedgerAsync("titan-cif", "CIF-001", Tenant, "PHP", CancellationToken.None);

        Assert.Equal(TitanBindOutcome.Bound, result.Outcome);
        Assert.Equal("ACC-SYN-1", result.LedgerAccountRef);
        Assert.False(result.LedgerRowWritten);
        var call = Assert.Single(calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://titan.synthetic.test/api/customer-directory/ledger-account/lookup", call.Url);
        AssertBody(call.Body, "titan-cif", "CIF-001", Tenant, currency: "PHP");
    }

    [Fact]
    public async Task Not_found_fails_closed_for_both_lookups()
    {
        var bind = Bind(new ScriptHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var customer = await bind.ResolveCustomerAsync("titan-cif", "CIF-404", Tenant, CancellationToken.None);
        var ledger = await bind.ResolveLedgerAsync("titan-cif", "CIF-404", Tenant, null, CancellationToken.None);

        AssertRefused(customer, "titan.customer.not_found");
        AssertRefused(ledger, "titan.ledger.not_found");
    }

    [Fact]
    public async Task Ambiguous_ledger_account_conflict_fails_closed()
    {
        var bind = Bind(new ScriptHandler(_ => Error(HttpStatusCode.Conflict, "LEDGER_ACCOUNT_AMBIGUOUS")));

        AssertRefused(await bind.ResolveLedgerAsync("titan-cif", "CIF-002", Tenant, null, CancellationToken.None), "titan.ledger.ambiguous");

        // A 409 with any other code is a plain refusal, still with no ledger reference.
        var other = Bind(new ScriptHandler(_ => Error(HttpStatusCode.Conflict, "SOMETHING_ELSE")));
        AssertRefused(await other.ResolveLedgerAsync("titan-cif", "CIF-002", Tenant, null, CancellationToken.None), "titan.ledger.refused");
    }

    [Fact]
    public async Task Tenant_mismatch_fails_closed_for_both_lookups()
    {
        var bind = Bind(new ScriptHandler(_ => Error(HttpStatusCode.Forbidden, "TENANT_MISMATCH")));

        AssertRefused(await bind.ResolveCustomerAsync("titan-cif", "CIF-001", Tenant, CancellationToken.None), "titan.customer.tenant_mismatch");
        AssertRefused(await bind.ResolveLedgerAsync("titan-cif", "CIF-001", Tenant, null, CancellationToken.None), "titan.ledger.tenant_mismatch");

        // Any other 403 (for example PLATFORM_ENTITLEMENT_DENIED) is a plain refusal.
        var denied = Bind(new ScriptHandler(_ => Error(HttpStatusCode.Forbidden, "PLATFORM_ENTITLEMENT_DENIED")));
        AssertRefused(await denied.ResolveCustomerAsync("titan-cif", "CIF-001", Tenant, CancellationToken.None), "titan.customer.refused");
        AssertRefused(await denied.ResolveLedgerAsync("titan-cif", "CIF-001", Tenant, null, CancellationToken.None), "titan.ledger.refused");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"ledgerAccountRef":"ACC-SYN-1","scheme":"titan-cif","id":"CIF-001"}""", false)] // not wrapped in data
    [InlineData(HttpStatusCode.OK, """{"data":{"scheme":"titan-cif","id":"CIF-OTHER","tenantId":"11111111-1111-1111-1111-111111111111","ledgerAccountRef":"ACC-SYN-1"}}""", false)] // another customer
    [InlineData(HttpStatusCode.OK, """{"data":{"scheme":"titan-cif","id":"CIF-001","tenantId":"22222222-2222-2222-2222-222222222222","ledgerAccountRef":"ACC-SYN-1"}}""", false)] // another tenant
    [InlineData(HttpStatusCode.OK, """{"data":{"scheme":"titan-cif","id":"CIF-001","tenantId":"11111111-1111-1111-1111-111111111111"}}""", true)] // a customer, but no account
    [InlineData(HttpStatusCode.OK, "not json", false)]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":"VALIDATION_ERROR"}}""", false)]
    [InlineData(HttpStatusCode.Unauthorized, "", false)]
    [InlineData(HttpStatusCode.Found, "", false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"GOVERNANCE_FAIL_CLOSED"}}""", false)]
    public async Task Any_other_answer_fails_closed(HttpStatusCode status, string body, bool customerBinds)
    {
        var bind = Bind(new ScriptHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }));

        AssertRefused(await bind.ResolveLedgerAsync("titan-cif", "CIF-001", Tenant, null, CancellationToken.None), "titan.ledger.refused");
        var customer = await bind.ResolveCustomerAsync("titan-cif", "CIF-001", Tenant, CancellationToken.None);
        if (customerBinds)
            Assert.Equal(TitanBindOutcome.Bound, customer.Outcome);
        else
            AssertRefused(customer, "titan.customer.refused");
    }

    [Fact]
    public async Task Transport_failure_fails_closed()
    {
        var bind = Bind(new ScriptHandler(_ => throw new HttpRequestException("synthetic outage")));

        AssertRefused(await bind.ResolveCustomerAsync("titan-cif", "CIF-001", Tenant, CancellationToken.None), "titan.customer.refused");
        AssertRefused(await bind.ResolveLedgerAsync("titan-cif", "CIF-001", Tenant, null, CancellationToken.None), "titan.ledger.refused");
    }

    private static TitanCustomerLedgerBind Bind(HttpMessageHandler handler) =>
        new(Config("PlatformIntegrated", "https://titan.synthetic.test", "https://titan.synthetic.test"), handler, new FixedToken("titan-service-token"));

    private static ScriptHandler Recording(List<(HttpMethod, string, string)> calls, HttpStatusCode status, string body) =>
        new(request =>
        {
            var content = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
            calls.Add((request.Method, request.RequestUri!.ToString(), content));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        });

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent($$$"""{"error":{"code":"{{{code}}}","message":"synthetic"}}""") };

    private static void AssertBody(string body, string scheme, string id, string tenant, string? currency)
    {
        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(scheme, root.GetProperty("scheme").GetString());
        Assert.Equal(id, root.GetProperty("id").GetString());
        Assert.Equal(tenant, root.GetProperty("tenantId").GetString());
        if (currency is null)
            Assert.False(root.TryGetProperty("currencyCode", out _));
        else
            Assert.Equal(currency, root.GetProperty("currencyCode").GetString());
    }

    private static void AssertRefused(TitanCustomerBindResult result, string code)
    {
        Assert.Equal(TitanBindOutcome.Refused, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Null(result.Id);
        Assert.Null(result.TenantId);
    }

    private static void AssertRefused(TitanLedgerBindResult result, string code)
    {
        Assert.Equal(TitanBindOutcome.Refused, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Null(result.LedgerAccountRef);
        Assert.False(result.LedgerRowWritten);
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
