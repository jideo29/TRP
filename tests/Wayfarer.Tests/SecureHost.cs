using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Wayfarer.Security;

namespace Wayfarer.Tests;

/// <summary>
/// Synthetic Aegis for host tests: an EC P-256 key (Aegis signs ES256) and an RSA key served as JWKS and an introspection endpoint that only answers the
/// <c>wayfarer-introspection</c> client over HTTP Basic (no caller token as Bearer, no tenant header).
/// </summary>
public sealed class FakeAegis : HttpMessageHandler
{
    public const string Issuer = "https://aegis.synthetic.test";
    public const string Audience = "wayfarer-api";
    public const string JwksUrl = Issuer + "/.well-known/jwks.json";
    public const string IntrospectionUrl = Issuer + "/connect/introspect";
    public const string IntrospectionSecret = "synthetic-introspection-secret";
    public const string KeyId = "synthetic-key-1";
    public const string EcKeyId = "synthetic-ec-key-1";

    private readonly RsaSecurityKey _key;
    private readonly ECDsaSecurityKey _ecKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = EcKeyId };

    public FakeAegis()
    {
        _key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = KeyId };
    }

    public ConcurrentBag<string> Revoked { get; } = [];
    public bool IntrospectionDown { get; set; }
    private int _jwksFetches;
    public int JwksFetches => Volatile.Read(ref _jwksFetches);
    public ConcurrentQueue<HttpRequestMessage> IntrospectionRequests { get; } = new();
    public ConcurrentQueue<string> IntrospectionBodies { get; } = new();

    public static IReadOnlyDictionary<string, string?> Settings(string environment) => new Dictionary<string, string?>
    {
        ["Deployment:Mode"] = environment == "Development" ? "Standalone" : "",
        ["Wayfarer:LivePayout"] = "false",
        ["Aegis:Issuer"] = Issuer,
        ["Aegis:JwksUrl"] = JwksUrl,
        ["Aegis:Audience"] = Audience,
        ["Aegis:IntrospectionUrl"] = IntrospectionUrl,
        ["Aegis:IntrospectionClientId"] = WayfarerAegisOptions.DefaultIntrospectionClientId,
        ["Aegis:IntrospectionClientSecret"] = IntrospectionSecret,
        ["urls"] = "https://127.0.0.1:8443",
        ["OpenApi:ExposeUi"] = "false",
    };

    public string Mint(
        string tenant = "tenant-1",
        string subject = "actor-1",
        string? customerRef = """{"scheme":"titan-cif","id":"cif-1","tenantId":"tenant-1"}""",
        TimeSpan? lifetime = null,
        SecurityKey? signWith = null,
        string? audience = null,
        bool es256 = false)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["tenantId"] = tenant,
            ["azp"] = "synthetic-client",
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (customerRef is not null)
            claims[AegisTokenRules.CustomerRefClaim] = customerRef;
        var now = DateTime.UtcNow;
        var expires = now + (lifetime ?? TimeSpan.FromMinutes(5));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? Audience,
            IssuedAt = expires < now ? expires.AddMinutes(-10) : now,
            NotBefore = expires < now ? expires.AddMinutes(-10) : now,
            Expires = expires,
            Claims = claims,
            TokenType = AegisTokenRules.AccessTokenType,
            SigningCredentials = es256
                ? new SigningCredentials(signWith ?? _ecKey, SecurityAlgorithms.EcdsaSha256)
                : new SigningCredentials(signWith ?? _key, SecurityAlgorithms.RsaSha256),
        });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == JwksUrl)
        {
            Interlocked.Increment(ref _jwksFetches);
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            var q = _ecKey.ECDsa.ExportParameters(false).Q;
            var json = JsonSerializer.Serialize(new
            {
                keys = new object[]
                {
                    new { kty = "EC", kid = EcKeyId, use = "sig", alg = "ES256", crv = "P-256", x = Base64UrlEncoder.Encode(q.X), y = Base64UrlEncoder.Encode(q.Y) },
                    new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E },
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        if (url == IntrospectionUrl && request.Method == HttpMethod.Post)
        {
            IntrospectionRequests.Enqueue(request);
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            IntrospectionBodies.Enqueue(form);
            if (IntrospectionDown)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                WayfarerAegisOptions.DefaultIntrospectionClientId + ":" + IntrospectionSecret));
            if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var token = Uri.UnescapeDataString(form.Split('&').First(p => p.StartsWith("token=", StringComparison.Ordinal))["token=".Length..]);
            var active = !Revoked.Contains(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(active ? """{"active":true}""" : """{"active":false}""", Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public static class SecureHost
{
    /// <summary>A host in <paramref name="environment"/> whose Aegis calls go to <paramref name="aegis"/>.</summary>
    public static WebApplicationFactory<Program> Create(
        FakeAegis aegis,
        string environment = "Production",
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?>(FakeAegis.Settings(environment));
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
            settings[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(collection =>
            {
                collection.AddHttpClient<AegisAccessTokenValidator>().ConfigurePrimaryHttpMessageHandler(() => aegis);
                collection.AddHttpClient<AegisRevocationChecker>().ConfigurePrimaryHttpMessageHandler(() => aegis);
                services?.Invoke(collection);
            });
        });
    }

    public static HttpClient Client(this WebApplicationFactory<Program> factory, string? bearer = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }
}
