using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Wayfarer.Tests;

public sealed class AegisAndStandaloneTests
{
    [Fact]
    public void Integrated_boot_stays_refused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Deployment:Mode"] = "PlatformIntegrated" })
            .Build();
        var act = () => FailClosedGate.EnsureCanBoot(configuration);
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void Missing_customerRef_is_rejected_and_sub_is_not_a_substitute()
    {
        var missing = Identity("actor-1", "tenant-1", "configured-audience", customerRef: null);
        Assert.False(AegisTokenRules.TryAccept(missing, out var failure));
        Assert.Contains("customerRef", failure, StringComparison.Ordinal);

        var incomplete = Identity("actor-1", "tenant-1", "configured-audience", """{"id":"cif-1","tenantId":"tenant-1"}""");
        Assert.False(AegisTokenRules.TryAccept(incomplete, out var incompleteFailure));
        Assert.Contains("scheme", incompleteFailure, StringComparison.Ordinal);
    }

    [Fact]
    public void Vulcan_Api_does_not_require_customerRef()
    {
        var identity = Identity("actor-1", "tenant-1", AegisTokenRules.VulcanAudience, customerRef: null);
        Assert.True(AegisTokenRules.TryAccept(identity, out var failure), failure);
    }

    [Fact]
    public void Complete_customerRef_is_accepted()
    {
        var identity = Identity(
            "actor-1",
            "tenant-1",
            "configured-audience",
            """{"scheme":"titan-cif","id":"cif-1","tenantId":"tenant-1"}""");
        Assert.True(AegisTokenRules.TryAccept(identity, out var failure), failure);
    }

    [Fact]
    public async Task Standalone_local_login_and_entitlement_stand_in_work()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Deployment:Mode"] = "Standalone",
                    ["Standalone:SigningKey"] = "wayfarer-standalone-test-key"
                });
            });
        });
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/standalone/session", new { operatorId = "wayfarer-operator" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var session = await created.Content.ReadFromJsonAsync<SessionBody>();
        Assert.Contains("standalone", session!.Label, StringComparison.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        using var who = await client.GetAsync("/api/standalone/session");
        Assert.Equal(HttpStatusCode.OK, who.StatusCode);

        using var entitlement = await client.GetAsync("/api/standalone/entitlement");
        Assert.Equal(HttpStatusCode.OK, entitlement.StatusCode);
        var body = await entitlement.Content.ReadAsStringAsync();
        Assert.Contains("Not an Atlas Allow", body, StringComparison.Ordinal);
        Assert.Contains("false", body, StringComparison.Ordinal);

        using var payout = await client.PostAsync("/api/remittance/payout", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, payout.StatusCode);
    }

    private static ClaimsIdentity Identity(string subject, string tenant, string audience, string? customerRef)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("tenantId", tenant),
            new("aud", audience),
        };
        if (customerRef is not null)
            claims.Add(new Claim(AegisTokenRules.CustomerRefClaim, customerRef));
        return new ClaimsIdentity(claims, "Bearer");
    }

    private sealed record SessionBody(string Token, string Label, string OperatorId);
}
