# Wayfarer (TRP)

Wayfarer is the remittance system of record, not Pulse (T-11).

This repository is a fail-closed host skeleton. Live payout is not implemented.

Nova remittance intents must come here later, not be absorbed by Pulse. The intake surface `POST /api/remittance/intents` exists so Nova can bind remittance here; it still fails closed and does not soft-Accept.

Pulse payment rails (Instapay, PesoNet, PDDTS, SWIFT, bills, QR, ecommerce, collections) are refused on this host. See [`architecture/t-11-not-pulse-rails.md`](architecture/t-11-not-pulse-rails.md) and [`manifest.json`](manifest.json).

The remittance journey is not unblocked. Journey Accept is not claimed. Wave D stays **PARKED**.

Equicom remains **HOLD**. There is no outbound email. Sell-open stays **FROZEN**. `money_pass` stays false.

## What this host does

| Surface | Behavior |
| --- | --- |
| `GET /api/host` | States that this process is the remittance system of record, not Pulse (T-11). Lists forbidden Pulse rails. |
| `GET /api/manifest` | Serves remittance SoR honesty (`pulseRailsAbsorbed=false`, journey `Blocked`). |
| `POST /api/remittance/payout` | Always fails closed. Pulse rail codes return `T11_PULSE_RAIL_ABSORPTION_REFUSED`. No payout is executed. |
| `POST /api/remittance/intents` | Nova remittance intent intake on Wayfarer (not Pulse). Always fails closed; journey stays Blocked. |
| `GET /health` | Process probe only. `money_pass` is false. The remittance journey stays blocked. |

Regulated deployment modes (`TrudiIntegrated`, `PlatformIntegrated`, `ForeignIntegrated`, `Production`), `Wayfarer:LivePayout=true`, `Wayfarer:AbsorbPulseRails=true`, and `Wayfarer:RemittanceJourneyUnblocked=true` refuse to boot. A Standalone skeleton may boot so the boundary can be read. Booting does not enable payout or unpark Accept.

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
- No soft Journey Accept / remittance unpark
- No prices or SKUs
- No Equicom send, outbound email, or sell-open
