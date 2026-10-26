using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Wayfarer.Tests;

public sealed class StatusPageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StatusPageTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Root_is_an_operator_status_page_that_keeps_payout_closed()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<title>Wayfarer — Remittance host</title>", html);
        Assert.Contains("Payout is closed", html);
        Assert.Contains("money_pass is false", html);
        Assert.Contains("bank_booked is false", html);
        Assert.Contains("settled is false", html);
        Assert.Contains("Live payout is not implemented.", html);
        Assert.Contains("This host does not publish an intent route.", html);
        Assert.Contains("An unbound peer is refused.", html);
        Assert.Contains("/assets/mark.svg", html);
        Assert.Contains("/assets/wordmark.svg", html);
        Assert.Contains("/assets/closed.svg", html);
        Assert.Contains("/assets/host.js", html);
        Assert.DoesNotContain("#F6F5F1", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ED1C24", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money_pass=true", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("money_pass: true", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HSM", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eKYC", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SIEM", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/assets/host.css")]
    [InlineData("/assets/host.js")]
    [InlineData("/assets/mark.svg")]
    [InlineData("/assets/wordmark.svg")]
    [InlineData("/assets/closed.svg")]
    [InlineData("/assets/swagger.css")]
    public async Task Static_assets_are_served(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("#F6F5F1", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#ED1C24", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Intent_route_is_not_on_this_host()
    {
        // The fallback policy answers 401 to an anonymous caller on any unmapped path; a signed-in caller sees 404.
        var client = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Standalone:SigningKey", "wayfarer-standalone-test-key")).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/remittance/intents")).StatusCode);

        using var session = await client.PostAsJsonAsync("/api/standalone/session", new { operatorId = "wayfarer-operator", tenantId = "tenant-1" });
        var token = (await session.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync("/api/remittance/intents");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_page_stays_available_when_swagger_is_hidden()
    {
        var aegis = new FakeAegis();
        using var factory = SecureHost.Create(aegis, Environments.Production);
        // Signed in, so an unmapped path answers 404 rather than the fallback policy's 401.
        var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }
}
