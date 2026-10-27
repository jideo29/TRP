# Wayfarer (TRP)

Wayfarer is the remittance system of record, not Pulse (T-11).

This repository is a fail-closed host skeleton. Live payout is not implemented.

Nova remittance intents must come here later, not be absorbed by Pulse.

The remittance journey is not unblocked. Journey Accept is not claimed.

Equicom remains **HOLD**. There is no outbound email. Sell-open stays **FROZEN**. `money_pass` stays false.

## What this host does

| Surface | Behavior |
| --- | --- |
| `GET /` | Operator status page. It reads this process. It does not send a payout. |
| `GET /api/host` | States that this process is the remittance system of record, not Pulse (T-11). |
| `POST /api/remittance/payout` | Requires an Aegis Bearer access token (M52): signature (ES256 only; RS256 and other algorithms are refused), issuer, audience and lifetime are validated against the Aegis JWKS before any peer call, and revocation is checked by introspection as the `wayfarer-introspection` client. The tenant comes from the token; a `customerRef.tenantId` that differs is refused with 403. Only the validated token is forwarded to Atlas, Sentinel and Pulse; no tenant, subject or decision header is sent. Requires `customerRef`. Toward Pulse at `POST /corp-pay/orchestrate` (the path Summit uses) when `Pulse:BaseUrl`, `Atlas:BaseUrl`, and `Sentinel:BaseUrl` are set. `channelProduct` is `instapay` or `pesonet`, with no default rail and no Titan posting. An empty Pulse URL stays NotConfigured. Integrated mode fails closed when any of those URLs is unset. The standalone stand-in is labeled SIMULATED. No settlement, `bank_booked`, or `money_pass`. Live payout is not implemented. |
| `POST /api/remittance/binding` | Requires a caller (L20). The customer scheme, ID and tenant are in the JSON body, the tenant must be the caller's, and the ledger reference is returned only when the token's `customerRef` names that customer (otherwise `titan.ledger.not_entitled`). Titan is called with Wayfarer's own client-credentials token (`Titan:ClientId`, `Titan:ClientSecret`), never the caller's. Titan's CustomerDirectoryPort is called with the CustomerRef only in the JSON body (ASVS 8.3.1): `POST /api/customer-directory/refs/lookup` and `POST /api/customer-directory/ledger-account/lookup` (optional `currencyCode` from the request body). `Titan:CustomerDirectory:BaseUrl` and `Titan:Ledger:BaseUrl` both name Titan CoreLedger. Every Titan error fails closed: 404 gives `titan.*.not_found`, 409 `LEDGER_ACCOUNT_AMBIGUOUS` gives `titan.ledger.ambiguous`, 403 `TENANT_MISMATCH` gives `titan.*.tenant_mismatch`, and anything else gives `titan.*.refused`, with no reference returned. |
| `GET /health` | Process probe only. `money_pass` is false. The remittance journey stays blocked. |

Regulated deployment modes (`TrudiIntegrated`, `PlatformIntegrated`, `ForeignIntegrated`, `Production`) and `Wayfarer:LivePayout=true` refuse to boot. A Standalone skeleton may boot so the boundary can be read. Booting does not enable payout.

## Security baseline

Every route except `/`, the static assets, `/api/host` and `/health` requires a caller. The OpenAPI document (`/openapi/v1.json`) and Swagger UI are served in Development only; no setting serves them in another environment, and the checked-in `docs/api/wayfarer-openapi-v1.json` is the published contract. Outside Development the host refuses to start when:

- `Deployment:Mode` is `Standalone` (X1, decision 117; only Development is exempt, `Testing` and `Staging` are not). The Standalone session routes (`/api/standalone/*`) are mapped only in Development, and a Standalone session expires after `Standalone:SessionMinutes` (default 60, at most 480);
- `Aegis:Issuer`, `Aegis:JwksUrl` or `Aegis:IntrospectionUrl` is not https, `Aegis:Audience` is empty, `Aegis:IntrospectionClientId` is not exactly `wayfarer-introspection`, or its secret is missing (X2, X5, W-1);
- a listener, or a Pulse, Atlas, Sentinel or Titan URL, is http (X3). The image listens on https 8443 only, and an http listener next to an https one is refused (W-2); the certificate is mounted at deploy time. The ASP.NET runtime base in `src/Wayfarer.Host/Dockerfile` is pinned by `sha256` digest (a test enforces it) and Dependabot proposes digest updates. CI compiles with the runner's .NET SDK and packs that publish output into the image; the image does not restore NuGet inside Docker;
- Titan is configured without `Titan:ClientId` and `Titan:ClientSecret` (L20);
- `RateLimiting:Enabled` is `false` (X7).

Rate limits (X7) apply after authentication, per client and tenant (per address when anonymous), and answer 429 with `Retry-After`. `X-Forwarded-For` and `X-Forwarded-Proto` are read only when `ForwardedHeaders:KnownProxies` or `ForwardedHeaders:KnownNetworks` names the gateway; with neither set, forwarded headers are ignored. Secrets (`Aegis:IntrospectionClientSecret`, `Titan:ClientSecret`) come from the environment or the secret store, never from committed files.

## Run

```bash
dotnet run --project src/Wayfarer.Host
```

The launch profile runs in Development on `https://localhost:7213` with the Standalone stand-in. Standalone sessions need `Standalone:SigningKey` (set it with `dotnet user-secrets`). This repository has no demo password.

```bash
dotnet test Wayfarer.sln
```

CI (`.github/workflows/ci.yml`, M48 / X4) runs the build with warnings as errors, the full test suite, a NuGet vulnerability audit, gitleaks over git history with `.gitleaks.toml`, Trivy filesystem and image scans, Semgrep, and a CycloneDX SBOM per image digest. SonarQube (`/d:sonar.token` from the `SONAR_TOKEN` secret, quality gate awaited) runs when that secret and an https `SONAR_HOST_URL` are set. When they are absent the scan job is skipped; that skip is not a quality-gate pass. Semgrep still fails the workflow on an ERROR finding. Dependabot covers NuGet, GitHub Actions and the Docker base image.

## Non-goals

- No live payout, corridor, or settlement
- No Pulse rail absorption (T-11)
- No prices or SKUs
- No Equicom send, outbound email, or sell-open
