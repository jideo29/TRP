using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Wayfarer.Tests;

public sealed class OpenApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OpenApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Document_declares_bearer_and_the_fail_closed_routes()
    {
        var json = await DevelopmentClient().GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var bearer = root.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        var paths = root.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/host", out _));
        Assert.True(paths.TryGetProperty("/health", out _));
        var payout = paths.GetProperty("/api/remittance/payout").GetProperty("post");
        Assert.True(payout.GetProperty("responses").TryGetProperty("503", out _));
        Assert.True(payout.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(payout.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(payout.GetProperty("responses").TryGetProperty("429", out _));
        Assert.False(payout.GetProperty("responses").TryGetProperty("200", out _));
        // M52: payout requires Bearer; it is not optional.
        var requirements = payout.GetProperty("security").EnumerateArray().ToList();
        Assert.Single(requirements);
        Assert.True(requirements[0].TryGetProperty("Bearer", out _));
        Assert.False(paths.TryGetProperty("/api/remittance/binding", out _));
        Assert.False(root.TryGetProperty("security", out var documentSecurity) && documentSecurity.GetArrayLength() > 0
            && documentSecurity.EnumerateArray().Any(r => !r.EnumerateObject().Any()));

        Assert.Equal("/", root.GetProperty("servers")[0].GetProperty("url").GetString());
        Assert.DoesNotContain("http://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Document_matches_the_committed_snapshot()
    {
        var live = await DevelopmentClient().GetStringAsync("/openapi/v1.json");
        var snapshotPath = Path.Combine(RepoRoot(), "docs", "api", "wayfarer-openapi-v1.json");
        var committed = File.ReadAllText(snapshotPath);

        Assert.Equal(Normalize(committed), Normalize(live));
    }

    [Fact]
    public async Task Swagger_ui_is_exposed_in_development_without_external_assets()
    {
        var response = await DevelopmentClient().GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("swagger-ui", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unpkg.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsdelivr.net", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("validator.swagger.io", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Document_and_swagger_ui_are_not_served_in_production()
    {
        var aegis = new FakeAegis();
        using var factory = SecureHost.Create(aegis, Environments.Production);
        // Signed in, so an unmapped path answers 404 rather than the fallback policy's 401.
        var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/scalar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_caller_gets_no_document_outside_development()
    {
        using var factory = SecureHost.Create(new FakeAegis(), Environments.Production);
        var client = factory.Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public async Task The_retired_expose_ui_setting_does_not_serve_the_document_outside_development()
    {
        var aegis = new FakeAegis();
        using var factory = SecureHost.Create(
            aegis, Environments.Production, new Dictionary<string, string?> { ["OpenApi:ExposeUi"] = "true" });
        var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    private HttpClient DevelopmentClient() => _factory.CreateClient();


    private static string Normalize(string json) => json.Replace("\r\n", "\n").Trim();

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
