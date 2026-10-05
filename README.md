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
| `POST /api/remittance/payout` | Always fails closed. No payout is executed. |
| `GET /health` | Process probe only. `money_pass` is false. The remittance journey stays blocked. |

Regulated deployment modes (`TrudiIntegrated`, `PlatformIntegrated`, `ForeignIntegrated`, `Production`) and `Wayfarer:LivePayout=true` refuse to boot. A Standalone skeleton may boot so the boundary can be read. Booting does not enable payout.

## Run

```bash
dotnet run --project src/Wayfarer.Host
```

The http launch profile listens on `http://localhost:5230`. This repository has no demo password.

```bash
dotnet test Wayfarer.sln
```

## Non-goals

- No live payout, corridor, or settlement
- No Pulse rail absorption (T-11)
- No prices or SKUs
- No Equicom send, outbound email, or sell-open
