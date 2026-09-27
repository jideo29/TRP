# Wayfarer (TRP)

Wayfarer is the remittance system of record, not Pulse (T-11).

This repository is a fail-closed host skeleton. Live payout is not implemented.

Nova remittance intents must come here later, not be absorbed by Pulse. The intake surface `POST /api/remittance/intents` exists so Nova can bind remittance here; it still fails closed and does not soft-Accept.

Pulse payment rails (Instapay, PesoNet, PDDTS, SWIFT, bills, QR, ecommerce, collections) are refused on this host. See [`architecture/t-11-not-pulse-rails.md`](architecture/t-11-not-pulse-rails.md) and [`manifest.json`](manifest.json).

**Risk / compliance deepen (Aegis + RiskPort):** Unconfigured RiskPort (Sentinel), Aegis IdentityPort, and Atlas EntitlementPort paths are **Fail**, not Pass. **Unknown ≠ Allow**. Soft Allow invent is refused. Journey Accept is not claimed.

**Money ports (ADR-004):** Unconfigured PostingPort (Titan / foreign CBS) and CustomerDirectoryPort are **Fail**, not Pass. Stub or local posting is not Allow. Wayfarer does not own GL (`LOCAL_GL_REFUSED`) and does not create a local CIF. Optional DocumentPort evidence claims are Fail while Scribe is NotConfigured. No outbound peer call. `money_pass` stays false.

The remittance journey is not unblocked. Journey Accept is not claimed. Wave D stays **PARKED**.

Equicom remains **HOLD**. There is no outbound email. Sell-open stays **FROZEN**. `money_pass` stays false.

Admin / back-office glass is out of scope on this host. When ops UI is pulled later it must use [`trudi-admin-template-shadcnprobase`](https://github.com/jideo29/trudi-admin-template-shadcnprobase.git) ([DEC-005](https://github.com/jideo29/trudi-banking-architecture/blob/main/decisions/DEC-005-admin-template-shadcnprobase.md)). This deepen does not ship admin glass.

## What this host does

| Surface | Behavior |
| --- | --- |
| `GET /api/host` | Remittance SoR identity (T-11) + risk/compliance honesty (`NotConfigured` peers). |
| `GET /api/manifest` | Serves remittance SoR honesty (`pulseRailsAbsorbed=false`, journey `Blocked`). |
| `GET /api/risk/status` | RiskPort / Aegis / Entitlement labeled `NotConfigured`. `complianceUnknownIsAllow=false`. |
| `POST /api/risk/decide` | Fail-not-Pass when RiskPort or Aegis is unconfigured. Soft Allow invent refused. |
| `POST /api/compliance/consult` | Unknown and Deny are not Allow. Unconfigured entitlement is Fail. |
| `POST /api/remittance/payout` | Pulse rail codes → `T11_PULSE_RAIL_ABSORPTION_REFUSED`. Otherwise risk/compliance Fail-not-Pass. No payout executed. |
| `POST /api/remittance/intents` | Nova remittance intent intake on Wayfarer (not Pulse). Always fails closed; journey stays Blocked. |
| `GET /api/posting/status` | PostingPort, CustomerDirectoryPort, and DocumentPort labeled `NotConfigured`. `money_pass` false. Admin glass not shipped. |
| `POST /api/posting/submit` | Unconfigured, stub, or labeled PostingPort is Fail. No outbound submit. No local GL. |
| `POST /api/customers/lookup` | Unconfigured CustomerDirectoryPort is Fail. No outbound lookup. |
| `POST /api/customers` | Local CIF create refused. |
| `POST` / `PUT` / `PATCH /api/ledger/balances` and `POST /api/balances` | `LOCAL_GL_REFUSED`. Balances do not change here. |
| `POST /api/documents/evidence` | Unconfigured DocumentPort evidence claim is Fail, not Allow. |
| `GET /health` | Process probe only. `money_pass` is false. Journey blocked. Peers `NotConfigured`. |

Regulated deployment modes (`TrudiIntegrated`, `PlatformIntegrated`, `ForeignIntegrated`, `Production`), `Wayfarer:LivePayout=true`, `Wayfarer:AbsorbPulseRails=true`, `Wayfarer:RemittanceJourneyUnblocked=true`, `Wayfarer:InventRiskAllow=true`, `Wayfarer:SoftCompliancePass=true`, `Wayfarer:ClaimMoneyPass=true`, `Wayfarer:OwnLocalGl=true`, and `Wayfarer:AllowStubPosting=true` refuse to boot. A Standalone skeleton may boot so the boundary can be read. Booting does not enable payout, invent a risk Pass, submit a posting, or unpark Accept.

## Run

```bash
dotnet run --project src/Wayfarer.Host
```

```bash
dotnet test Wayfarer.sln
```

## Non-goals

- No live payout, corridor, or settlement
- No Pulse rail absorption (T-11)
- No live PostingPort submit and no outbound peer call
- No local GL and no local CIF
- No soft Journey Accept / remittance unpark
- No soft Journey Accept / risk Pass invent
- No `money_pass` invent
- No Unknown-as-Allow on compliance consult, posting, customer lookup, or evidence
- No prices or SKUs
- No Equicom send, outbound email, or sell-open
- No admin glass in this deepen (DEC-005 cited only as future SoT)
