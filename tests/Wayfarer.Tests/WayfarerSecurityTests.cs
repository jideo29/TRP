using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Wayfarer.Security;

namespace Wayfarer.Tests;

/// <summary>Review fixes 2026-10-26: M52, L20, X1, X2, X3, X5, X7 and the round-2 lessons, on a Production host.</summary>
public sealed class WayfarerSecurityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static StringContent PayoutBody(string tenant = "tenant-1") => new(
        JsonSerializer.Serialize(new
        {
            customerRef = new { scheme = "titan-cif", id = "cif-1", tenantId = tenant },
            rail = "instapay",
            amountPhp = 25m
        }, Json),
        Encoding.UTF8,
        "application/json");

    [Fact]
    public async Task Payout_refuses_an_anonymous_caller_with_401_and_a_bearer_challenge()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        using var client = factory.Client();

        var response = await client.PostAsync("/api/remittance/payout", PayoutBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Payout_refuses_forged_expired_wrong_audience_and_unsigned_tokens()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        var otherKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = FakeAegis.KeyId };
        var valid = aegis.Mint();
        var parts = valid.Split('.');
        var unsigned = parts[0] + "." + parts[1] + ".";

        foreach (var token in new[]
                 {
                     aegis.Mint(signWith: otherKey),
                     aegis.Mint(lifetime: TimeSpan.FromMinutes(-10)),
                     aegis.Mint(audience: "another-api"),
                     unsigned,
                     "not-a-token",
                 })
        {
            using var client = factory.Client(token);
            var response = await client.PostAsync("/api/remittance/payout", PayoutBody());
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Es256_tokens_signed_with_the_aegis_ec_key_are_accepted()
    {
        // Round 3: Aegis (TISP KeySetSessionTokenSigner) signs ES256 on P-256 and publishes EC keys in its JWKS.
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        var token = aegis.Mint(es256: true);
        Assert.Equal("ES256", new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).Alg);
        using (var client = factory.Client(token))
        {
            var response = await client.PostAsync("/api/remittance/payout", PayoutBody());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // An ES256 signature from another P-256 key under the published kid, or naming the RSA key, is refused.
        var otherEc = new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = FakeAegis.EcKeyId };
        foreach (var refused in new[] { aegis.Mint(es256: true, signWith: otherEc), aegis.Mint(es256: true, signWith: new ECDsaSecurityKey(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = FakeAegis.KeyId }) })
        {
            using var client = factory.Client(refused);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody())).StatusCode);
        }
    }

    [Fact]
    public async Task A_token_without_a_tenant_is_refused_even_when_the_body_names_one()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        using var client = factory.Client(aegis.Mint(tenant: "", customerRef: null));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody(tenant: "tenant-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/remittance/binding",
            new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" })).StatusCode);
    }

    [Fact]
    public async Task Only_es256_and_rs256_are_accepted()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        var symmetric = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-hs256-key-that-is-long-enough-32b")) { KeyId = FakeAegis.KeyId };
        var hs256 = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = FakeAegis.Issuer,
            Audience = FakeAegis.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            TokenType = AegisTokenRules.AccessTokenType,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "actor-1",
                ["tenantId"] = "tenant-1",
                [AegisTokenRules.CustomerRefClaim] = """{"scheme":"titan-cif","id":"cif-1","tenantId":"tenant-1"}""",
            },
            SigningCredentials = new SigningCredentials(symmetric, SecurityAlgorithms.HmacSha256),
        });
        using var client = factory.Client(hs256);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody())).StatusCode);
    }

    [Fact]
    public async Task Unknown_kid_tokens_do_not_refetch_the_jwks_on_every_request()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        for (var i = 0; i < 20; i++)
        {
            var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "unknown-" + i };
            using var client = factory.Client(aegis.Mint(signWith: key));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody())).StatusCode);
        }

        Assert.InRange(aegis.JwksFetches, 1, 2);
    }

    [Fact]
    public async Task An_unreachable_introspection_endpoint_fails_closed()
    {
        var aegis = new FakeAegis { IntrospectionDown = true };
        await using var factory = SecureHost.Create(aegis);
        using var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody())).StatusCode);
    }

    [Fact]
    public async Task A_wrong_introspection_secret_is_refused_by_aegis_and_the_caller_is_refused()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis, overrides: new Dictionary<string, string?>
        {
            ["Aegis:IntrospectionClientSecret"] = "a-different-synthetic-secret",
        });
        using var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/remittance/payout", PayoutBody())).StatusCode);
        var sent = Assert.Single(aegis.IntrospectionRequests);
        Assert.Equal("Basic", sent.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Payout_refuses_a_body_tenant_that_differs_from_the_token()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        using var client = factory.Client(aegis.Mint(tenant: "tenant-1"));

        var response = await client.PostAsync("/api/remittance/payout", PayoutBody(tenant: "tenant-2"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("wayfarer.tenant_mismatch", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Valid_token_reaches_the_fail_closed_payout_and_introspection_uses_the_wayfarer_client()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        var token = aegis.Mint();
        using var client = factory.Client(token);

        var response = await client.PostAsync("/api/remittance/payout", PayoutBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(body.GetProperty("payoutExecuted").GetBoolean());
        Assert.False(body.GetProperty("moneyPass").GetBoolean());

        var introspection = Assert.Single(aegis.IntrospectionRequests);
        Assert.Equal("Basic", introspection.Headers.Authorization!.Scheme);
        Assert.Equal(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("wayfarer-introspection:" + FakeAegis.IntrospectionSecret)),
            introspection.Headers.Authorization.Parameter);
        Assert.False(introspection.Headers.Contains("X-Tenant-Id"));
        var form = Assert.Single(aegis.IntrospectionBodies);
        Assert.Contains("token_type_hint=access_token", form, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        var token = aegis.Mint();
        aegis.Revoked.Add(token);
        using var client = factory.Client(token);

        var response = await client.PostAsync("/api/remittance/payout", PayoutBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Payout_forwards_only_the_validated_token_and_no_identity_headers()
    {
        var aegis = new FakeAegis();
        var peers = new PeerRecorder();
        var settings = new Dictionary<string, string?>
        {
            ["Pulse:BaseUrl"] = "https://pulse.synthetic.test",
            ["Atlas:BaseUrl"] = "https://atlas.synthetic.test",
            ["Atlas:Environment"] = "Production",
            ["Sentinel:BaseUrl"] = "https://sentinel.synthetic.test",
        };
        await using var factory = SecureHost.Create(aegis, overrides: settings, services: services =>
        {
            services.RemoveAll<RemittancePayout>();
            services.AddSingleton(sp => new RemittancePayout(sp.GetRequiredService<IConfiguration>(), peers));
        });
        var token = aegis.Mint();
        using var client = factory.Client(token);

        var response = await client.PostAsync("/api/remittance/payout", PayoutBody());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, peers.Requests.Count);
        Assert.All(peers.Requests, request =>
        {
            Assert.Equal("Bearer " + token, request.Authorization);
            Assert.Empty(request.IdentityHeaders);
        });
        // The tenant reaches Sentinel from the token, not from a header.
        Assert.Contains("\"tenantId\":\"tenant-1\"", peers.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Binding_needs_a_caller_checks_the_tenant_and_withholds_the_ledger_from_an_unentitled_caller()
    {
        var aegis = new FakeAegis();
        var bind = new FakeBind();
        await using var factory = SecureHost.Create(aegis, services: services =>
        {
            services.RemoveAll<ITitanCustomerLedgerBind>();
            services.AddSingleton<ITitanCustomerLedgerBind>(bind);
        });
        var request = new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" };

        using (var anonymous = factory.Client())
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/remittance/binding", request)).StatusCode);

        using (var otherTenant = factory.Client(aegis.Mint(tenant: "tenant-2", customerRef: """{"scheme":"titan-cif","id":"cif-1","tenantId":"tenant-2"}""")))
            Assert.Equal(HttpStatusCode.Forbidden, (await otherTenant.PostAsJsonAsync("/api/remittance/binding", request)).StatusCode);

        using (var entitled = factory.Client(aegis.Mint()))
        {
            var body = await (await entitled.PostAsJsonAsync("/api/remittance/binding", request)).Content.ReadFromJsonAsync<RemittanceBinding>(Json);
            Assert.Equal("ledger-synthetic-1", body!.Ledger.LedgerAccountRef);
            Assert.Equal(1, bind.LedgerCalls);
        }

        using (var notEntitled = factory.Client(aegis.Mint(customerRef: """{"scheme":"titan-cif","id":"cif-9","tenantId":"tenant-1"}""")))
        {
            var body = await (await notEntitled.PostAsJsonAsync("/api/remittance/binding", request)).Content.ReadFromJsonAsync<RemittanceBinding>(Json);
            Assert.Equal("titan.ledger.not_entitled", body!.Ledger.Code);
            Assert.Null(body.Ledger.LedgerAccountRef);
            Assert.Equal(1, bind.LedgerCalls);
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void Standalone_mode_refuses_to_start_outside_development(string environment)
    {
        using var factory = SecureHost.Create(new FakeAegis(), environment, new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "Standalone",
            ["Standalone:SigningKey"] = "wayfarer-standalone-test-key",
        });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Standalone", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standalone_session_routes_are_not_mapped_outside_development()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis);
        using var client = factory.Client(aegis.Mint());

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/standalone/session", new { operatorId = "x", tenantId = "tenant-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/standalone/entitlement")).StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    public void Outside_development_http_listeners_and_http_aegis_urls_are_refused(string environment)
    {
        foreach (var overrides in new[]
                 {
                     new Dictionary<string, string?> { ["urls"] = "http://127.0.0.1:8080" },
                     new Dictionary<string, string?> { ["urls"] = "" },
                     new Dictionary<string, string?> { ["Aegis:JwksUrl"] = "http://aegis.synthetic.test/jwks" },
                     new Dictionary<string, string?> { ["Aegis:Issuer"] = "http://aegis.synthetic.test" },
                     new Dictionary<string, string?> { ["Aegis:IntrospectionClientSecret"] = "" },
                     new Dictionary<string, string?> { ["Pulse:BaseUrl"] = "http://pulse.synthetic.test" },
                     new Dictionary<string, string?> { ["RateLimiting:Enabled"] = "false" },
                     new Dictionary<string, string?> { ["Titan:Ledger:BaseUrl"] = "https://titan.synthetic.test" },
                 })
        {
            using var factory = SecureHost.Create(new FakeAegis(), environment, overrides);
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        }
    }

    [Fact]
    public async Task The_limiter_answers_429_with_retry_after_and_health_is_not_limited()
    {
        await using var factory = SecureHost.Create(new FakeAegis(), overrides: new Dictionary<string, string?>
        {
            ["RateLimiting:AnonymousPermitLimit"] = "2",
            ["RateLimiting:PermitLimit"] = "2",
        });
        using var client = factory.Client();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/host")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/host")).StatusCode);
        var limited = await client.GetAsync("/api/host");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task With_no_gateway_configured_x_forwarded_for_cannot_pick_the_anonymous_bucket()
    {
        await using var factory = SecureHost.Create(new FakeAegis(), overrides: new Dictionary<string, string?>
        {
            ["RateLimiting:AnonymousPermitLimit"] = "2",
        });
        using var client = factory.Client();

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 1; i <= 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/host");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{i}");
            last = (await client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    [Fact]
    public void Forwarded_headers_are_read_only_with_a_configured_gateway_and_bad_entries_refuse_start()
    {
        IConfiguration Config(string? proxies, string? networks) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WayfarerForwardedHeaders.KnownProxiesSetting] = proxies,
                [WayfarerForwardedHeaders.KnownNetworksSetting] = networks,
            }).Build();

        Assert.False(WayfarerForwardedHeaders.TrustsForwardedHeaders(Config(null, null)));
        Assert.False(WayfarerForwardedHeaders.TrustsForwardedHeaders(Config("", " ")));
        Assert.True(WayfarerForwardedHeaders.TrustsForwardedHeaders(Config("10.0.0.5", null)));
        Assert.True(WayfarerForwardedHeaders.TrustsForwardedHeaders(Config(null, "10.0.0.0/24")));

        using var factory = SecureHost.Create(new FakeAegis(), overrides: new Dictionary<string, string?>
        {
            [WayfarerForwardedHeaders.KnownProxiesSetting] = "not-an-address",
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task An_unhandled_error_returns_problem_details_without_exception_detail()
    {
        var aegis = new FakeAegis();
        await using var factory = SecureHost.Create(aegis, services: services =>
        {
            services.RemoveAll<ITitanCustomerLedgerBind>();
            services.AddSingleton<ITitanCustomerLedgerBind>(new ThrowingBind());
        });
        using var client = factory.Client(aegis.Mint());

        var response = await client.PostAsJsonAsync("/api/remittance/binding", new { scheme = "titan-cif", id = "cif-1", tenantId = "tenant-1" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic failure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ThrowingBind", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_standalone_session_expires_and_a_future_session_is_refused()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [StandaloneLocalSession.SigningKeyConfig] = "wayfarer-standalone-test-key",
            [StandaloneLocalSession.SessionMinutesConfig] = "30",
        }).Build();
        var issued = new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        Assert.True(StandaloneLocalSession.TryIssue(configuration, "operator-1", "tenant-1", issued, out var token, out _));

        Assert.True(StandaloneLocalSession.TryRead(configuration, token, issued.AddMinutes(29), out _, out var tenant));
        Assert.Equal("tenant-1", tenant);
        Assert.False(StandaloneLocalSession.TryRead(configuration, token, issued.AddMinutes(31), out _, out _));
        Assert.False(StandaloneLocalSession.TryRead(configuration, token, issued.AddMinutes(-5), out _, out _));
    }

    private sealed record PeerRequest(string Path, string? Authorization, List<string> IdentityHeaders, string Body);

    private sealed class PeerRecorder : HttpMessageHandler
    {
        public List<PeerRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var identity = new[] { "X-Tenant-Id", "X-Subject-Id", "X-Authorization-Decision-Id" }
                .Where(name => request.Headers.Contains(name)).ToList();
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new PeerRequest(request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), identity, body));
            var path = request.RequestUri!.AbsolutePath;
            var json = path.Contains("consultation", StringComparison.Ordinal)
                ? """{"isAllowed":true,"decision":"Allow"}"""
                : path.Contains("risk/decisions", StringComparison.Ordinal)
                    ? """{"outcome":"Allow"}"""
                    : """{"transferId":"pulse-synthetic-1"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeBind : ITitanCustomerLedgerBind
    {
        public int LedgerCalls;

        public Task<TitanCustomerBindResult> ResolveCustomerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new TitanCustomerBindResult(TitanBindOutcome.Bound, "titan.customer.bound", "TITAN", scheme, id, tenantId, false));

        public Task<TitanLedgerBindResult> ResolveLedgerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LedgerCalls);
            return Task.FromResult(new TitanLedgerBindResult(TitanBindOutcome.Bound, "titan.ledger.bound", "TITAN", "ledger-synthetic-1", false));
        }
    }

    private sealed class ThrowingBind : ITitanCustomerLedgerBind
    {
        public Task<TitanCustomerBindResult> ResolveCustomerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic failure");

        public Task<TitanLedgerBindResult> ResolveLedgerAsync(string scheme, string id, string tenantId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic failure");
    }
}
