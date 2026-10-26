using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Wayfarer.Security;

/// <summary>
/// M52 / L20 / ASVS 1.4.1, 4.1.1: authentication for every route except the process probe, the host statement,
/// the OpenAPI document and the static status page. The fallback policy requires an authenticated caller, so a
/// new route is closed unless it opts out explicitly.
/// <list type="bullet">
/// <item><see cref="AegisScheme"/>: an Aegis access token validated by <see cref="AegisAccessTokenValidator"/>
/// and, when <c>Aegis:IntrospectionUrl</c> is set (required outside Development), checked for revocation (X2).</item>
/// <item><see cref="StandaloneScheme"/>: the Wayfarer Standalone local session. X1: accepted only when the host
/// environment is Development and <c>Deployment:Mode</c> is Standalone. It is never forwarded to a peer.</item>
/// </list>
/// </summary>
public static class WayfarerAuthentication
{
    public const string Scheme = "Wayfarer";
    public const string AegisScheme = "Aegis";
    public const string StandaloneScheme = "WayfarerStandalone";
    public const string TenantClaim = "tenantId";
    public const string LegacyTenantClaim = "tenant_id";
    public const string AegisTokenItem = "Wayfarer.AegisAccessToken";

    public static IServiceCollection AddWayfarerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WayfarerAegisOptions>(configuration.GetSection(WayfarerAegisOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WayfarerAegisOptions>>().Value;
            return new AegisSigningKeyCache(
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromSeconds(options.JwksRefreshMinSeconds),
                TimeSpan.FromMinutes(10));
        });
        services.AddHttpClient<AegisAccessTokenValidator>(client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient<AegisRevocationChecker>(client => client.Timeout = TimeSpan.FromSeconds(10));

        services
            .AddAuthentication(Scheme)
            .AddPolicyScheme(Scheme, Scheme, options =>
            {
                // A Standalone session has two dot-separated parts; a JWT has three.
                options.ForwardDefaultSelector = context =>
                    BearerToken(context.Request) is { } token && token.Split('.').Length == 2
                        ? StandaloneScheme
                        : AegisScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, AegisAuthenticationHandler>(AegisScheme, null)
            .AddScheme<AuthenticationSchemeOptions, StandaloneSessionAuthenticationHandler>(StandaloneScheme, null);

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build();
            options.DefaultPolicy = options.FallbackPolicy;
        });
        return services;
    }

    public static string? BearerToken(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var parsed)
            || !string.Equals(parsed.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(parsed.Parameter))
        {
            return null;
        }

        return parsed.Parameter.Trim();
    }

    public static bool StandaloneAllowed(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() && TitanCustomerLedgerBind.IsStandalone(configuration["Deployment:Mode"]);
}

/// <summary>
/// The authenticated caller as the endpoints see it. Tenant and subject come from the validated token only;
/// a request body never supplies them (M52, ASVS 4.1.2). <see cref="AegisAccessToken"/> is set only for a
/// validated Aegis token; it is the only credential Wayfarer forwards to Atlas, Sentinel and Pulse.
/// </summary>
public sealed record WayfarerCaller(
    string Subject,
    string? TenantId,
    IReadOnlyList<CustomerRefBody> CustomerRefs,
    string? AegisAccessToken,
    bool Standalone)
{
    public static WayfarerCaller? From(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return null;

        var subject = user.FindFirst("sub")?.Value?.Trim();
        if (string.IsNullOrEmpty(subject))
            return null;

        var tenant = user.FindFirst(WayfarerAuthentication.TenantClaim)?.Value?.Trim();
        if (string.IsNullOrEmpty(tenant))
            tenant = user.FindFirst(WayfarerAuthentication.LegacyTenantClaim)?.Value?.Trim();

        var standalone = string.Equals(user.Identity.AuthenticationType, WayfarerAuthentication.StandaloneScheme, StringComparison.Ordinal);
        var token = !standalone && context.Items.TryGetValue(WayfarerAuthentication.AegisTokenItem, out var value) ? value as string : null;
        return new WayfarerCaller(subject, string.IsNullOrEmpty(tenant) ? null : tenant, ReadCustomerRefs(user), token, standalone);
    }

    /// <summary>True when the token names this customer in its ADR-003 <c>customerRef</c>.</summary>
    public bool Holds(string? scheme, string? id, string? tenantId) =>
        CustomerRefs.Any(r =>
            string.Equals(r.Scheme, scheme?.Trim(), StringComparison.Ordinal)
            && string.Equals(r.Id, id?.Trim(), StringComparison.Ordinal)
            && string.Equals(r.TenantId, tenantId?.Trim(), StringComparison.Ordinal));

    private static IReadOnlyList<CustomerRefBody> ReadCustomerRefs(ClaimsPrincipal user)
    {
        var refs = new List<CustomerRefBody>();
        foreach (var claim in user.FindAll(AegisTokenRules.CustomerRefClaim))
        {
            try
            {
                using var document = JsonDocument.Parse(claim.Value);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    Add(document.RootElement);
                else if (document.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var item in document.RootElement.EnumerateArray())
                        Add(item);
            }
            catch (JsonException)
            {
                // AegisTokenRules already refused a malformed customerRef; nothing to add.
            }
        }

        return refs;

        void Add(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;
            var scheme = Read(element, "scheme");
            var id = Read(element, "id");
            var tenantId = Read(element, "tenantId");
            if (scheme is not null && id is not null && tenantId is not null)
                refs.Add(new CustomerRefBody(scheme, id, tenantId));
        }

        static string? Read(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
    }
}

public sealed class AegisAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly AegisAccessTokenValidator _validator;
    private readonly IOptions<WayfarerAegisOptions> _aegis;

    public AegisAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AegisAccessTokenValidator validator,
        IOptions<WayfarerAegisOptions> aegis)
        : base(options, logger, encoder)
    {
        _validator = validator;
        _aegis = aegis;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrEmpty(Request.Headers.Authorization.ToString()))
            return AuthenticateResult.NoResult();

        var token = WayfarerAuthentication.BearerToken(Request);
        if (token is null)
            return AuthenticateResult.Fail("A Bearer access token is required.");

        var options = _aegis.Value;
        if (!options.Configured)
            return AuthenticateResult.Fail("Aegis is not configured. Fail closed.");

        var decision = await _validator.ValidateAsync(token, options, Context.RequestAborted).ConfigureAwait(false);
        if (!decision.Accepted || decision.Identity is null)
        {
            Logger.LogInformation("Aegis token refused: {Failure}", decision.Failure);
            return AuthenticateResult.Fail(decision.Failure.ToString());
        }

        var revocation = Context.RequestServices.GetRequiredService<AegisRevocationChecker>();
        if (revocation.Enabled)
        {
            var status = await revocation.CheckAsync(
                token,
                decision.TokenId,
                decision.ExpiresAt ?? DateTimeOffset.UtcNow,
                Context.RequestAborted).ConfigureAwait(false);
            if (status != RevocationStatus.Active)
            {
                var failure = status == RevocationStatus.Revoked ? AegisTokenFailure.Revoked : AegisTokenFailure.RevocationUnavailable;
                Logger.LogWarning("Aegis token refused: {Failure}", failure);
                return AuthenticateResult.Fail(failure.ToString());
            }
        }

        Context.Items[WayfarerAuthentication.AegisTokenItem] = token;
        var principal = new ClaimsPrincipal(decision.Identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, WayfarerAuthentication.AegisScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

public sealed class StandaloneSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _clock;

    public StandaloneSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration,
        IHostEnvironment environment,
        TimeProvider clock)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
        _environment = environment;
        _clock = clock;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = WayfarerAuthentication.BearerToken(Request);
        if (token is null)
            return Task.FromResult(AuthenticateResult.NoResult());

        if (!WayfarerAuthentication.StandaloneAllowed(_environment, _configuration))
            return Task.FromResult(AuthenticateResult.Fail("Standalone sessions are accepted only in Development."));

        if (!StandaloneLocalSession.TryRead(_configuration, token, _clock.GetUtcNow(), out var operatorId, out var tenantId))
            return Task.FromResult(AuthenticateResult.Fail("Standalone session is invalid or expired."));

        var claims = new List<Claim> { new("sub", operatorId) };
        if (!string.IsNullOrWhiteSpace(tenantId))
            claims.Add(new Claim(WayfarerAuthentication.TenantClaim, tenantId));
        var identity = new ClaimsIdentity(claims, WayfarerAuthentication.StandaloneScheme, "sub", "roles");
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), WayfarerAuthentication.StandaloneScheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
