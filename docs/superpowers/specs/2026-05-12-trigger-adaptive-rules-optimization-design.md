# Trigger Adaptive Rules Optimization

> Date: 2026-05-12
> Game: Stellar Blade (剑星)
> Hardware: FlyDigi Apex 5 (八爪鱼5)

## Problem

Trigger adaptive feedback stutters — effects continuously reset every 5ms engine tick, producing jarring pulsing instead of smooth tactile feedback. Additionally, LT (aim) and RT (shoot) effects cannot coexist because the engine only applies one rule per tick.

## Root Cause

1. **No per-side dedup**: `ApplyEffect()` sends the full 6-packet ForceAdapt sequence unconditionally, even when the same `(side, mode)` is already active. At 200Hz, this restarts the effect 200 times per second.
2. **Single-rule bottleneck**: `EvaluateRules()` picks one global highest-priority rule — LT and RT cannot have independent effects simultaneously.

## Design

### Engine: MappingEngine.cs

**Change 1 — Per-side effect dedup**

Track `(side, mode)` for each active trigger. Before sending, check if the target side already has the same mode active — skip if identical.

Keeps `_activeLtMode` / `_activeRtMode` already present in the engine loop; add a check at the top of the ForceAdapt branch in `ApplyEffect()`.

**Change 2 — Per-side independent rule evaluation**

Replace the single `bestRule` selection loop with two independent passes:

1. LT pass: iterate all rules, filter to those targeting `Left` or `Both`, pick highest priority → apply to LT
2. RT pass: iterate all rules, filter to those targeting `Right` or `Both`, pick highest priority → apply to RT

Each side dispatches its own effect independently.

**Change 3 — Remove default rumble fallback**

The fallback `SetTriggerRumble(20, 20)` / `SetTriggerRumble(0, 0)` in `EvaluateRules` is removed. Only explicit rules drive trigger behavior.

### Profile: stellar_blade.json

Three rules:

| # | ID | Condition | Priority | Target | Mode |
|---|-----|-----------|----------|--------|------|
| 1 | `ads_lt` | aiming_and_shooting | 110 | Left | Racing |
| 2 | `ads_rt` | aiming_and_shooting | 110 | Right | Sniper |
| 3 | `aiming_lt` | aiming | 70 | Left | Racing |

**Runtime behavior**:

| Scene | LT | RT |
|-------|----|----|
| Aiming only (L2) | Racing (progressive damping) | Off |
| Aiming + Shooting (L2+R2) | Racing (progressive damping) | Sniper (trigger breakthrough) |
| All other states | Off | Off |

### Key feel change

RT switches from **Machinegun** (vibration burst, restarts every tick) to **Sniper** (resistance buildup → breakthrough release). Combined with dedup, the trigger break fires once and holds — clean, decisive, matching DS5 Stellar Blade feel.

### Files changed

- `src/StellarForceAdapt/Mapping/MappingEngine.cs` — per-side dedup, independent LT/RT evaluation, remove default rumble
- `src/StellarForceAdapt/Profiles/stellar_blade.json` — three rules replacing current three rules

### Not changed

- `TriggerProfile.cs` / `TriggerEffect` data model — no new fields needed
- `ForceAdaptProtocol.cs` — no new modes
- `FlyDigiDevice.cs` — no new send logic
