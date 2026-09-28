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
        Assert.False(payout.GetProperty("responses").TryGetProperty("200", out _));

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
    public async Task Swagger_ui_returns_404_in_production()
    {
        using var factory = FactoryFor(Environments.Production, exposeUi: false);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/scalar")).StatusCode);

        var document = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    [Fact]
    public async Task Swagger_ui_can_be_enabled_outside_development_by_config()
    {
        using var factory = FactoryFor(Environments.Production, exposeUi: true);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient DevelopmentClient() => _factory.CreateClient();

    private WebApplicationFactory<Program> FactoryFor(string environment, bool exposeUi) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("OpenApi:ExposeUi", exposeUi ? "true" : "false");
            builder.UseSetting("Deployment:Mode", "Standalone");
            builder.UseSetting("Wayfarer:LivePayout", "false");
        });

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
