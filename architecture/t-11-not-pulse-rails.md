# T-11 — Wayfarer ≠ Pulse rails

**As-of:** 2026-09-27  
**Status:** Fail-closed scaffold honesty (not Journey Accept)

Wayfarer (`jideo29/TRP`) is the remittance payout system of record. Pulse (`jideo29/TPCP`) owns payment / collections rails. Remittance must not be absorbed into Pulse. Pulse rail absorption is refused on this host — Pulse rails must not be executed here.

## Doctrine

| Owner | Owns | Does not own |
| --- | --- | --- |
| Wayfarer | Remittance payout orchestration SoR | Instapay, PesoNet, PDDTS, SWIFT reporting, bills, QR, ecommerce, collections |
| Pulse | Payment / collections rail orchestration | Remittance payout SoR |

## Host honesty

- `manifest.json` sets `systemOfRecord.remittancePayout=wayfarer` and `honesty.pulseRailsAbsorbed=false`.
- `POST /api/remittance/payout` and `POST /api/remittance/intents` refuse known Pulse rail codes with `T11_PULSE_RAIL_ABSORPTION_REFUSED`.
- Nova remittance intents are accepted **here** as intake only and still fail closed — they are not absorbed by Pulse.
- `Wayfarer:AbsorbPulseRails=true` and `Wayfarer:RemittanceJourneyUnblocked=true` refuse to boot.
- Remittance journey stays **Blocked**. Wave D stays **PARKED**. Journey Accept is **not** claimed.

## Explicit non-claims

- Live payout is not implemented.
- Equicom remains **HOLD**. Sell-open stays **FROZEN**. `money_pass` stays false.
- This note does not unpark Wayfarer Accept.
