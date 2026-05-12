# User-Facing Release Design

## Summary

Create `release/user-facing` branch: strip debug tooling, game-specific logic, and dead code.
Result is a generic FlyDigi ForceAdapt trigger adapter — XInput input + JSON rules → HID trigger commands.

## File Changes

### Remove (3 source files)
- `Monitor/StellarBladeMonitor.cs` — game process detection + action inference
- `Monitor/CeDataSource.cs` — Cheat Engine memory bridge
- `Mapping/ControllerMapping.cs` — button binding (UI disabled, XInput incompatible)

### Remove (non-code)
- USBPcap captures, python scripts, DEVELOPMENT.md, build_result.txt, spacestation_cmds*.txt, debug_pcapng.py
- `.qoder/` directory

### Modify (5 source files + 1 XAML + 1 theme)

**TriggerProfile.cs** — `TriggerCondition` fields become:
- `Buttons` (ushort, must all match), `ButtonsAny` (ushort, any match)
- `LeftTriggerMin/Max`, `RightTriggerMin/Max` (byte, 0-255)
- `LeftStickMagnitudeMin`, `RightStickMagnitudeMin` (short)
- Remove: `PlayerActionCondition`, `PlayerAction`, `DetectionSource` enums; CE-data fields; Action/InCombat/ComboMin

**MappingEngine.cs:**
- Remove `StellarBladeMonitor`/`CeDataSource` dependency
- Subscribe `XInputWatcher.StateChanged` directly
- `EvaluateRules()` evaluates raw XInput fields against conditions
- Remove `OnGameStateChanged`, `OnGameProcessChanged`, action inference

**FlyDigiDevice.cs:**
- Remove CD2 input read path in `WatchdogLoop` (`isCd2` branch)
- Keep heartbeat-only loop for all device types
- Remove `HIDGamepadReader` references (already dead)

**MainWindow.xaml:**
- Remove right-side TabControl (debug tab + log tab)
- Remove binding UI block (already Visibility=Collapsed)
- Simplify to: top status bar, profile panel, trigger preview, start/stop button, status bar

**MainWindow.xaml.cs:**
- Remove console attachment P/Invoke + EnsureConsoleAttached
- Remove debug display (UpdateDebugDisplay, SetButtonLight, RawHidText etc.)
- Remove button binding stubs (BindButton_Click, CheckBinding, SaveMapping_Click, ResetMapping_Click)
- Remove V2 manual test methods (V2ApplyEffect_Click, V2ClearBoth_Click)
- Remove file logging (log.txt write), keep simple in-app ListBox log
- Remove OpenLogFile_Click
- Remove game monitor / CE events wiring

## New JSON Config Format

```jsonc
{
  "name": "Example",
  "version": "1.0",
  "description": "Generic trigger adapter profile",
  "rules": [{
    "id": "rule_id",
    "name": "Rule name",
    "priority": 100,
    "cooldown_ms": 0,
    "condition": {
      "buttons": 0,
      "buttons_any": 0,
      "left_trigger_min": 0,
      "left_trigger_max": 255,
      "right_trigger_min": 0,
      "right_trigger_max": 255,
      "left_stick_magnitude_min": 0,
      "right_stick_magnitude_min": 0
    },
    "effect": {
      "type": "force_adapt",
      "mode": "racing",
      "target": "both",
      "duration_ms": 0,
      "intensity": 220,
      "speed": 100
    }
  }]
}
```

## XInput Button Bitmask Reference

| Button | Mask   |
|--------|--------|
| A      | 0x1000 |
| B      | 0x2000 |
| X      | 0x4000 |
| Y      | 0x8000 |
| LB     | 0x0100 |
| RB     | 0x0200 |
| Start  | 0x0010 |
| Back   | 0x0020 |
| L3     | 0x0040 |
| R3     | 0x0080 |
| D-Up   | 0x0001 |
| D-Down | 0x0002 |
| D-Left | 0x0004 |
| D-Right| 0x0008 |

## Architecture After Cleanup

```
XInputWatcher (polling, ~250Hz)
    │  StateChanged event
    ▼
MappingEngine (rule evaluation, ~200Hz)
    │  ApplyTriggerEffect()
    ▼
FlyDigiDevice (HID USB output, 32/65-byte reports)
```

No process detection. No CE bridge. No game-specific action mapping.
The user writes JSON rules against raw XInput state.
