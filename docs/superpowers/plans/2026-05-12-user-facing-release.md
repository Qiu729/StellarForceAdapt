# User-Facing Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a clean `release/user-facing` branch: generic FlyDigi ForceAdapt trigger adapter with no debug tooling, no game-specific code, JSON rules against raw XInput.

**Architecture:** `XInputWatcher` (~250Hz) → `MappingEngine` (~200Hz rule evaluation) → `FlyDigiDevice` (HID USB output). No process detection, no CE bridge, no game-specific action mapping.

**Tech Stack:** .NET 9 WPF, HidSharp, XInput P/Invoke (xinput1_4.dll), C# 13

---

### Task 1: Create release branch

**Files:** None (git operation)

- [ ] **Step 1: Create and switch to release/user-facing branch**

```bash
git checkout -b release/user-facing
```

Expected: branch created, switched to it.

- [ ] **Step 2: Verify we're on the right branch**

```bash
git branch --show-current
```

Expected: `release/user-facing`

---

### Task 2: Remove dead source files

**Files:**
- Delete: `src/StellarForceAdapt/Monitor/StellarBladeMonitor.cs`
- Delete: `src/StellarForceAdapt/Monitor/CeDataSource.cs`
- Delete: `src/StellarForceAdapt/Mapping/ControllerMapping.cs`

- [ ] **Step 1: Delete the three source files**

```bash
rm src/StellarForceAdapt/Monitor/StellarBladeMonitor.cs
rm src/StellarForceAdapt/Monitor/CeDataSource.cs
rm src/StellarForceAdapt/Mapping/ControllerMapping.cs
```

- [ ] **Step 2: Remove non-code diagnostic files**

```bash
rm -f "USBPcap捕获内容.pcapng" "USBPcap捕获内容-v2.pcapng" "USBPcap捕获结果.txt" "USBPcap捕获结果-v2.txt"
rm -f spacestation_cmds.txt spacestation_cmds_v2.txt build_result.txt
rm -f parse_usb.py parse_usb2.py parse_usb3.py parse_spacestation_cmds.py debug_pcapng.py
rm -f DEVELOPMENT.md
rm -rf .qoder
```

- [ ] **Step 3: Commit removal of dead files**

```bash
git add -A
git commit -m "$(cat <<'EOF'
chore: remove game monitor, CE bridge, controller mapping, and diagnostic files

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Simplify TriggerProfile.cs — replace game-specific conditions with raw XInput

**Files:**
- Modify: `src/StellarForceAdapt/Mapping/TriggerProfile.cs`

- [ ] **Step 1: Rewrite TriggerProfile.cs**

Replace entire file content with:

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StellarForceAdapt.Mapping;

/// <summary>
/// Defines a complete trigger profile with mapping rules.
/// </summary>
public class TriggerProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("rules")]
    public List<MappingRule> Rules { get; set; } = [];

    public static TriggerProfile? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TriggerProfile>(json, s_jsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Profile] Failed to load {path}: {ex.Message}");
            return null;
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, s_jsonOptions);
        File.WriteAllText(path, json);
    }

    public static List<(string Path, TriggerProfile Profile)> LoadAll(string directory)
    {
        var result = new List<(string, TriggerProfile)>();
        if (!Directory.Exists(directory)) return result;

        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            var profile = Load(file);
            if (profile != null && profile.Rules.Count > 0)
                result.Add((file, profile));
        }
        return result;
    }

    private static readonly JsonSerializerOptions s_jsonOptions;

    static TriggerProfile()
    {
        s_jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        s_jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    }
}

/// <summary>
/// One mapping rule: when condition is met, trigger an effect.
/// </summary>
public class MappingRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("condition")]
    public TriggerCondition Condition { get; set; } = new();

    [JsonPropertyName("effect")]
    public TriggerEffect Effect { get; set; } = new();

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    [JsonPropertyName("cooldown_ms")]
    public int CooldownMs { get; set; } = 0;
}

/// <summary>
/// Conditions evaluated against raw XInput state.
/// All numeric conditions use 0/null = "don't care".
/// </summary>
public class TriggerCondition
{
    /// <summary>All of these buttons must be pressed (XInput bitmask). 0 = don't care.</summary>
    [JsonPropertyName("buttons")]
    public ushort Buttons { get; set; }

    /// <summary>Any of these buttons must be pressed (XInput bitmask). 0 = don't care.</summary>
    [JsonPropertyName("buttons_any")]
    public ushort ButtonsAny { get; set; }

    [JsonPropertyName("left_trigger_min")]
    public byte LeftTriggerMin { get; set; }

    [JsonPropertyName("left_trigger_max")]
    public byte LeftTriggerMax { get; set; } = 255;

    [JsonPropertyName("right_trigger_min")]
    public byte RightTriggerMin { get; set; }

    [JsonPropertyName("right_trigger_max")]
    public byte RightTriggerMax { get; set; } = 255;

    /// <summary>Minimum left stick magnitude (0-32768). 0 = don't care.</summary>
    [JsonPropertyName("left_stick_magnitude_min")]
    public short LeftStickMagnitudeMin { get; set; }

    /// <summary>Minimum right stick magnitude (0-32768). 0 = don't care.</summary>
    [JsonPropertyName("right_stick_magnitude_min")]
    public short RightStickMagnitudeMin { get; set; }
}

/// <summary>
/// A trigger effect to apply.
/// </summary>
public class TriggerEffect
{
    [JsonPropertyName("type")]
    public EffectType Type { get; set; } = EffectType.ForceAdapt;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "racing";

    [JsonPropertyName("position")]
    public byte Position { get; set; }

    [JsonPropertyName("intensity")]
    public byte Intensity { get; set; } = 128;

    [JsonPropertyName("speed")]
    public byte Speed { get; set; } = 128;

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("target")]
    public TriggerTarget Target { get; set; } = TriggerTarget.Both;

    [JsonPropertyName("sequence")]
    public List<TriggerEffect>? Sequence { get; set; }
}

public enum EffectType
{
    None,
    ForceAdapt,
    Rumble,
    Sequence,
}

public enum TriggerTarget
{
    Left,
    Right,
    Both,
}

public class RuleState
{
    public MappingRule Rule { get; init; } = null!;
    public DateTime LastTriggered { get; set; } = DateTime.MinValue;

    public bool CanTrigger()
    {
        if (Rule.CooldownMs <= 0) return true;
        return (DateTime.UtcNow - LastTriggered).TotalMilliseconds >= Rule.CooldownMs;
    }

    public void Triggered()
    {
        LastTriggered = DateTime.UtcNow;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/StellarForceAdapt/Mapping/TriggerProfile.cs
git commit -m "$(cat <<'EOF'
refactor: replace game-specific conditions with raw XInput fields in TriggerProfile

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Refactor MappingEngine.cs — remove game monitor dependency

**Files:**
- Modify: `src/StellarForceAdapt/Mapping/MappingEngine.cs`

- [ ] **Step 1: Rewrite MappingEngine.cs**

Replace entire file content with:

```csharp
using System.Diagnostics;
using StellarForceAdapt.HID;
using StellarForceAdapt.Monitor;

namespace StellarForceAdapt.Mapping;

/// <summary>
/// Core engine: evaluates XInput state against JSON rules, sends trigger commands.
/// </summary>
public class MappingEngine : IDisposable
{
    private readonly FlyDigiDevice _device;
    private readonly CancellationTokenSource _cts = new();

    private Thread? _engineThread;
    private List<RuleState> _activeRules = [];
    private bool _running;

    private ForceAdaptProtocol.ForceAdaptMode? _activeLtMode;
    private ForceAdaptProtocol.ForceAdaptMode? _activeRtMode;
    private DateTime _ltExpiry = DateTime.MinValue;
    private DateTime _rtExpiry = DateTime.MinValue;

    private readonly XInputWatcher _xinput = new();

    public TriggerProfile? CurrentProfile { get; private set; }
    public bool IsRunning => _running;
    public XInputState CurrentInput => _xinput.CurrentState;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? EffectTriggered;

    public MappingEngine(FlyDigiDevice device)
    {
        _device = device;
        _xinput.StateChanged += OnXInputStateChanged;
    }

    public void SetProfile(TriggerProfile profile)
    {
        CurrentProfile = profile;
        _activeRules = profile.Rules
            .OrderByDescending(r => r.Priority)
            .Select(r => new RuleState { Rule = r })
            .ToList();
        StatusChanged?.Invoke(this, $"Profile loaded: {profile.Name}");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _xinput.Start(4);

        _engineThread = new Thread(EngineLoop)
        {
            IsBackground = true,
            Name = "Mapping-Engine"
        };
        _engineThread.Start();

        StatusChanged?.Invoke(this, "Engine started");
        Debug.WriteLine("[Engine] Started");
    }

    public void Stop()
    {
        _running = false;
        _xinput.Stop();

        _device.ResetTriggers();
        _activeLtMode = null;
        _activeRtMode = null;

        StatusChanged?.Invoke(this, "Engine stopped");
        Debug.WriteLine("[Engine] Stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts.Cancel();
        _cts.Dispose();
        _xinput.StateChanged -= OnXInputStateChanged;
        _xinput.Dispose();
    }

    private void EngineLoop()
    {
        while (_running)
        {
            try
            {
                if (CurrentProfile != null && _device.IsConnected)
                    EvaluateRules();
                Thread.Sleep(5);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Engine] Loop error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private void EvaluateRules()
    {
        var xstate = _xinput.CurrentState;
        if (!xstate.Connected) return;

        // Expiry safety net
        if (_activeLtMode != null && DateTime.UtcNow >= _ltExpiry)
        {
            _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.LT, ForceAdaptProtocol.ForceAdaptMode.Off);
            _activeLtMode = null;
        }
        if (_activeRtMode != null && DateTime.UtcNow >= _rtExpiry)
        {
            _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.RT, ForceAdaptProtocol.ForceAdaptMode.Off);
            _activeRtMode = null;
        }

        var bestLt = FindBestRule(xstate, ForceAdaptProtocol.TriggerSide.LT);
        var bestRt = FindBestRule(xstate, ForceAdaptProtocol.TriggerSide.RT);

        if (bestLt == null && _activeLtMode != null)
        {
            _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.LT, ForceAdaptProtocol.ForceAdaptMode.Off);
            _activeLtMode = null;
        }
        if (bestRt == null && _activeRtMode != null)
        {
            _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.RT, ForceAdaptProtocol.ForceAdaptMode.Off);
            _activeRtMode = null;
        }

        if (bestRt != null)
        {
            if (ApplyEffect(bestRt.Effect))
                MarkRuleTriggered(bestRt);
        }
        if (bestLt != null && bestLt != bestRt)
        {
            if (ApplyEffect(bestLt.Effect))
                MarkRuleTriggered(bestLt);
        }
    }

    private MappingRule? FindBestRule(XInputState xstate, ForceAdaptProtocol.TriggerSide side)
    {
        MappingRule? bestRule = null;
        int bestPriority = int.MinValue;

        foreach (var ruleState in _activeRules)
        {
            if (!ruleState.CanTrigger()) continue;
            if (!EvaluateCondition(ruleState.Rule.Condition, xstate)) continue;

            var target = ruleState.Rule.Effect.Target;
            bool matchesSide = target == TriggerTarget.Both
                || (side == ForceAdaptProtocol.TriggerSide.LT && target == TriggerTarget.Left)
                || (side == ForceAdaptProtocol.TriggerSide.RT && target == TriggerTarget.Right);
            if (!matchesSide) continue;

            if (ruleState.Rule.Priority > bestPriority)
            {
                bestPriority = ruleState.Rule.Priority;
                bestRule = ruleState.Rule;
            }
        }

        return bestRule;
    }

    private void MarkRuleTriggered(MappingRule rule)
    {
        var ruleState = _activeRules.Find(r => r.Rule.Id == rule.Id);
        ruleState?.Triggered();
        EffectTriggered?.Invoke(this, rule.Name);
    }

    private static bool EvaluateCondition(TriggerCondition cond, XInputState state)
    {
        // Buttons (all must match)
        if (cond.Buttons != 0 && (state.Buttons & cond.Buttons) != cond.Buttons)
            return false;

        // Buttons (any match)
        if (cond.ButtonsAny != 0 && (state.Buttons & cond.ButtonsAny) == 0)
            return false;

        // Left trigger range
        if (state.LeftTrigger < cond.LeftTriggerMin || state.LeftTrigger > cond.LeftTriggerMax)
            return false;

        // Right trigger range
        if (state.RightTrigger < cond.RightTriggerMin || state.RightTrigger > cond.RightTriggerMax)
            return false;

        // Left stick magnitude
        if (cond.LeftStickMagnitudeMin > 0)
        {
            double mag = Math.Sqrt((long)state.LeftThumbX * state.LeftThumbX + (long)state.LeftThumbY * state.LeftThumbY);
            if (mag < cond.LeftStickMagnitudeMin) return false;
        }

        // Right stick magnitude
        if (cond.RightStickMagnitudeMin > 0)
        {
            double mag = Math.Sqrt((long)state.RightThumbX * state.RightThumbX + (long)state.RightThumbY * state.RightThumbY);
            if (mag < cond.RightStickMagnitudeMin) return false;
        }

        return true;
    }

    private bool ApplyEffect(TriggerEffect effect)
    {
        if (!_device.IsConnected) return false;

        switch (effect.Type)
        {
            case EffectType.ForceAdapt:
            {
                var mode = effect.Mode?.Trim().ToLowerInvariant() switch
                {
                    "off" or "none" or "clear" => ForceAdaptProtocol.ForceAdaptMode.Off,
                    "racing" or "pushback" or "damp" or "damping" => ForceAdaptProtocol.ForceAdaptMode.Racing,
                    "machinegun" or "burst" or "mg" => ForceAdaptProtocol.ForceAdaptMode.Machinegun,
                    "sniper" or "breakthrough" => ForceAdaptProtocol.ForceAdaptMode.Sniper,
                    "lock" or "triggerlock" or "resistance" => ForceAdaptProtocol.ForceAdaptMode.TriggerLock,
                    "vibrate" or "vibration" or "haptic" => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                    _ => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                };

                ForceAdaptProtocol.TriggerSide? side = effect.Target switch
                {
                    TriggerTarget.Left  => ForceAdaptProtocol.TriggerSide.LT,
                    TriggerTarget.Right => ForceAdaptProtocol.TriggerSide.RT,
                    _                   => null,
                };

                var expiry = effect.DurationMs > 0
                    ? DateTime.UtcNow.AddMilliseconds(effect.DurationMs)
                    : DateTime.MaxValue;

                if (side.HasValue)
                {
                    var currentMode = side.Value == ForceAdaptProtocol.TriggerSide.LT
                        ? _activeLtMode : _activeRtMode;
                    if (currentMode == mode)
                    {
                        if (side.Value == ForceAdaptProtocol.TriggerSide.LT)
                            _ltExpiry = expiry;
                        else
                            _rtExpiry = expiry;
                        return false;
                    }
                    var (ok, details) = _device.ApplyTriggerEffect(side.Value, mode);
                    FlyDigiDevice.Log?.Invoke($"Engine [{side.Value} {mode}]: {(ok ? "OK" : "FAIL")} {details}");
                    if (side.Value == ForceAdaptProtocol.TriggerSide.LT)
                    { _activeLtMode = mode; _ltExpiry = expiry; }
                    else
                    { _activeRtMode = mode; _rtExpiry = expiry; }
                }
                else
                {
                    if (_activeLtMode == mode && _activeRtMode == mode)
                    {
                        _ltExpiry = expiry;
                        _rtExpiry = expiry;
                        return false;
                    }
                    var (ok, details) = _device.ApplyTriggerEffectBoth(mode);
                    FlyDigiDevice.Log?.Invoke($"Engine [Both {mode}]: {(ok ? "OK" : "FAIL")} {details}");
                    _activeLtMode = mode; _ltExpiry = expiry;
                    _activeRtMode = mode; _rtExpiry = expiry;
                }
                return true;
            }

            case EffectType.Rumble:
            {
                byte left = effect.Target is TriggerTarget.Left or TriggerTarget.Both ? effect.Intensity : (byte)0;
                byte right = effect.Target is TriggerTarget.Right or TriggerTarget.Both ? effect.Intensity : (byte)0;
                _device.SetTriggerRumble(left, right);

                if (effect.DurationMs > 0)
                {
                    _ = Task.Delay(effect.DurationMs).ContinueWith(_ =>
                    {
                        if (_running) _device.SetTriggerRumble(0, 0);
                    });
                }
                return true;
            }

            case EffectType.Sequence:
            {
                if (effect.Sequence != null)
                    _ = PlaySequence(effect.Sequence);
                return true;
            }
        }
        return false;
    }

    private async Task PlaySequence(List<TriggerEffect> sequence)
    {
        foreach (var step in sequence)
        {
            ApplyEffect(step);
            if (step.DurationMs > 0)
                await Task.Delay(step.DurationMs);
        }
    }

    private void OnXInputStateChanged(object? sender, XInputState state)
    {
        // Engine evaluates rules at its own pace in EngineLoop;
        // XInputWatcher just keeps CurrentState fresh.
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/StellarForceAdapt/Mapping/MappingEngine.cs
git commit -m "$(cat <<'EOF'
refactor: remove game monitor dependency from MappingEngine, use raw XInput directly

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Simplify FlyDigiDevice.cs — remove CD2 input read path

**Files:**
- Modify: `src/StellarForceAdapt/HID/FlyDigiDevice.cs`

- [ ] **Step 1: Replace WatchdogLoop method**

The CD2 input-read branch (lines ~430-470) is removed. The entire `WatchdogLoop` becomes:

```csharp
    private void WatchdogLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;

        // Heartbeat-only: XInput handles all input reading.
        while (!token.IsCancellationRequested)
        {
            _streamLock.Wait(token);
            try { if (_stream == null) break; }
            finally { _streamLock.Release(); }

            try { token.WaitHandle.WaitOne(2000); }
            catch { break; }
        }
    }
```

Use Edit to replace the old `WatchdogLoop` method (from the `// APPROACH C:` comment line through the end of the method) with the above.

- [ ] **Step 2: Remove the `DisableCd2Reads` constant and CD2-read commentary**

Remove lines:
```csharp
    // APPROACH C: set to true to disable CD2 input reads entirely.
    // When the charging dock is connected, CD2 input reports arrive continuously
    // and HidStream interleaving (read outside lock + write inside lock) corrupts
    // the non-thread-safe stream, breaking ForceAdapt output. Disabling reads is
    // the simplest fix but loses SpaceStation coexistence keep-alive.
    private const bool DisableCd2Reads = false;
```

- [ ] **Step 3: Remove unused `using System.Diagnostics;` if it becomes unused**

Check if `Debug` is still used in FlyDigiDevice.cs (yes — `Debug.WriteLine` calls remain). No change needed.

- [ ] **Step 4: Commit**

```bash
git add src/StellarForceAdapt/HID/FlyDigiDevice.cs
git commit -m "$(cat <<'EOF'
refactor: remove CD2 input read path from FlyDigiDevice WatchdogLoop

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Clean MainWindow.xaml — remove debug tab, simplify UI

**Files:**
- Modify: `src/StellarForceAdapt/MainWindow.xaml`

- [ ] **Step 1: Replace MainWindow.xaml**

Replace entire file content with:

```xml
<Window x:Class="StellarForceAdapt.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="StellarForceAdapt — FlyDigi 自适应扳机"
        Height="520" Width="700"
        MinHeight="400" MinWidth="500"
        WindowStartupLocation="CenterScreen">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <!-- Top Bar -->
        <Border Grid.Row="0" Background="{StaticResource CardBg}" CornerRadius="8" Padding="16" Margin="0,0,0,12">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center">
                    <TextBlock Text="&#x26A1;" FontSize="24" Margin="0,0,8,0" />
                    <StackPanel>
                        <TextBlock Text="StellarForceAdapt" FontSize="20" FontWeight="Bold" />
                        <TextBlock Text="FlyDigi ForceAdapt 扳机适配器" FontSize="12"
                                   Foreground="{StaticResource TextSecondaryBrush}" />
                    </StackPanel>
                </StackPanel>

                <!-- Status Indicators -->
                <StackPanel Grid.Column="1" Orientation="Horizontal" HorizontalAlignment="Center" VerticalAlignment="Center">
                    <Border x:Name="ControllerStatus" Background="{StaticResource BgMedium}"
                            CornerRadius="16" Padding="12,4" Margin="4">
                        <TextBlock x:Name="ControllerText" Text="&#x1F3AE; 手柄: 未连接" FontSize="13" />
                    </Border>
                    <Border x:Name="EngineStatus" Background="{StaticResource BgMedium}"
                            CornerRadius="16" Padding="12,4" Margin="4">
                        <TextBlock x:Name="EngineText" Text="&#x2699; 引擎: 停止" FontSize="13" />
                    </Border>
                </StackPanel>

                <Button Grid.Column="2" x:Name="ToggleButton"
                        Content="&#x25B6; 启动引擎"
                        Click="ToggleEngine_Click"
                        Width="120" Height="36"
                        FontWeight="Bold" />
            </Grid>
        </Border>

        <!-- Main Content -->
        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel>
                <!-- Profile Selection -->
                <Border Background="{StaticResource CardBg}" CornerRadius="8" Padding="16" Margin="0,0,0,8">
                    <StackPanel>
                        <TextBlock Text="&#x1F4CB; 配置文件" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,8" />
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <ComboBox Grid.Column="0" x:Name="ProfileCombo"
                                      DisplayMemberPath="Name"
                                      SelectionChanged="Profile_Changed"
                                      Height="32" />
                            <Button Grid.Column="1" Content="&#x1F504; 刷新" Margin="8,0,0,0"
                                    Click="RefreshProfiles_Click" Width="70" />
                        </Grid>
                        <TextBlock x:Name="ProfileDesc" TextWrapping="Wrap" Margin="0,4,0,0"
                                   Foreground="{StaticResource TextSecondaryBrush}" FontSize="12" />
                    </StackPanel>
                </Border>

                <!-- Trigger Preview -->
                <Border Background="{StaticResource CardBg}" CornerRadius="8" Padding="16" Margin="0,0,0,8">
                    <StackPanel>
                        <TextBlock Text="&#x1F3AE; 扳机状态" FontSize="16" FontWeight="SemiBold" Margin="0,0,0,8" />
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>

                            <Border Grid.Column="0" Background="{StaticResource BgMedium}"
                                    CornerRadius="6" Padding="12">
                                <StackPanel>
                                    <TextBlock Text="L2 (左扳机)" FontSize="13"
                                               Foreground="{StaticResource TextSecondaryBrush}" />
                                    <ProgressBar x:Name="LeftTriggerBar" Height="12" Margin="0,4" Maximum="255" />
                                    <TextBlock x:Name="LeftTriggerValue" Text="0" FontSize="28"
                                               FontWeight="Bold" HorizontalAlignment="Center" Margin="0,4" />
                                </StackPanel>
                            </Border>

                            <TextBlock Grid.Column="1" Text="" VerticalAlignment="Center"
                                       Margin="16" Foreground="{StaticResource TextSecondaryBrush}" FontSize="18" />

                            <Border Grid.Column="2" Background="{StaticResource BgMedium}"
                                    CornerRadius="6" Padding="12">
                                <StackPanel>
                                    <TextBlock Text="R2 (右扳机)" FontSize="13"
                                               Foreground="{StaticResource TextSecondaryBrush}" />
                                    <ProgressBar x:Name="RightTriggerBar" Height="12" Margin="0,4" Maximum="255" />
                                    <TextBlock x:Name="RightTriggerValue" Text="0" FontSize="28"
                                               FontWeight="Bold" HorizontalAlignment="Center" Margin="0,4" />
                                </StackPanel>
                            </Border>
                        </Grid>
                    </StackPanel>
                </Border>

                <!-- Event Log -->
                <Border Background="{StaticResource CardBg}" CornerRadius="8" Padding="16" Margin="0,0,0,8">
                    <StackPanel>
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="&#x1F4DD; 事件日志" FontSize="16" FontWeight="SemiBold"
                                       VerticalAlignment="Center" />
                            <Button Grid.Column="1" Content="&#x1F5D1; 清空" Click="ClearLog_Click"
                                    Height="28" FontSize="12" Width="60" />
                        </Grid>
                        <Border Background="{StaticResource BgMedium}" CornerRadius="4" Padding="4"
                                Height="120" Margin="0,4,0,0">
                            <ListBox x:Name="LogList" Background="Transparent" BorderThickness="0"
                                     ScrollViewer.VerticalScrollBarVisibility="Auto">
                                <ListBox.ItemTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding}" TextWrapping="Wrap" FontSize="12"
                                                   Foreground="{StaticResource TextSecondaryBrush}" Margin="2" />
                                    </DataTemplate>
                                </ListBox.ItemTemplate>
                            </ListBox>
                        </Border>
                    </StackPanel>
                </Border>
            </StackPanel>
        </ScrollViewer>

        <!-- Status Bar -->
        <Border Grid.Row="2" Background="{StaticResource CardBg}" CornerRadius="8"
                Padding="12,6" Margin="0,8,0,0">
            <TextBlock x:Name="StatusText" Text="就绪" FontSize="12" />
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 2: Commit**

```bash
git add src/StellarForceAdapt/MainWindow.xaml
git commit -m "$(cat <<'EOF'
refactor: remove debug tab from MainWindow, simplify to user-facing layout

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Clean MainWindow.xaml.cs — remove debug, console, test code

**Files:**
- Modify: `src/StellarForceAdapt/MainWindow.xaml.cs`

- [ ] **Step 1: Rewrite MainWindow.xaml.cs**

Replace entire file content with:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StellarForceAdapt.HID;
using StellarForceAdapt.Mapping;
using StellarForceAdapt.Monitor;

namespace StellarForceAdapt;

public partial class MainWindow : Window
{
    private readonly FlyDigiDevice _device = new();
    private readonly MappingEngine _engine;
    private readonly List<(string Path, TriggerProfile Profile)> _profiles = [];
    private readonly string _profilesDir;

    private bool _isRunning;
    private bool _isReconnecting;
    private int _logCount;

    public MainWindow()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var baseDir = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles"))
            ? AppDomain.CurrentDomain.BaseDirectory
            : exeDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _profilesDir = Path.Combine(baseDir, "Profiles");

        _engine = new MappingEngine(_device);

        // Wire events
        _device.ConnectionChanged += OnControllerConnectionChanged;
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.EffectTriggered += OnEffectTriggered;

        // Initial scans
        RefreshProfiles_Click(null!, null!);
        ScanController();
        StartUiTimer();

        System.Diagnostics.Debug.WriteLine("[UI] Initialized");
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Stop();
        _device.Dispose();
        _engine.Dispose();
        base.OnClosed(e);
    }

    private void StartUiTimer()
    {
        var timer = new System.Timers.Timer(50);
        timer.Elapsed += (_, _) =>
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateTriggerDisplay();
                    UpdateStatusBar();
                });
            }
            catch { }
        };
        timer.Start();
    }

    private void UpdateTriggerDisplay()
    {
        var state = _engine.CurrentInput;
        if (!state.Connected) return;

        LeftTriggerBar.Value = state.LeftTrigger;
        RightTriggerBar.Value = state.RightTrigger;
        LeftTriggerValue.Text = state.LeftTrigger.ToString();
        RightTriggerValue.Text = state.RightTrigger.ToString();
    }

    private void UpdateStatusBar()
    {
        var state = _engine.CurrentInput;
        var connected = state.Connected;

        ControllerStatus.Background = connected
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : (SolidColorBrush)FindResource("BgMedium");
        ControllerText.Text = connected
            ? $"\U0001F3AE 手柄: 已连接 ({_device.DeviceName ?? "FlyDigi"})"
            : "\U0001F3AE 手柄: 未连接";

        EngineStatus.Background = _isRunning
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(244, 67, 54));
        EngineText.Text = _isRunning ? "⚙ 引擎: 运行中" : "⚙ 引擎: 停止";
    }

    private void OnControllerConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                Log("✅ 手柄已连接");
                SetStatus("控制器已就绪");
            }
            else
            {
                Log("❌ 手柄断开连接");
                SetStatus("手柄断开，正在重试...");
                if (_isReconnecting) return;
                _isReconnecting = true;
                _ = Task.Run(async () =>
                {
                    while (!_device.IsConnected && _isReconnecting)
                    {
                        await Task.Delay(2000);
                        if (_device.TryReconnect())
                        {
                            Dispatcher.Invoke(() => Log("✅ 手柄已重连"));
                            break;
                        }
                    }
                    _isReconnecting = false;
                });
            }
        });
    }

    private void OnEngineStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus(status);
            if (status.Contains("Profile"))
                Log($"ℹ {status}");
        });
    }

    private void OnEffectTriggered(object? sender, string effectName)
    {
        Dispatcher.Invoke(() => Log($"⚡ 触发: {effectName}"));
    }

    private void ToggleEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            StopEngine();
        else
            StartEngine();
    }

    private void StartEngine()
    {
        if (!_device.IsConnected)
        {
            if (!_device.Connect())
            {
                MessageBox.Show("无法连接到手柄，请确保 FlyDigi 手柄已通过 USB 连接", "连接失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (ProfileCombo.SelectedItem is TriggerProfile profile)
            _engine.SetProfile(profile);
        else if (_profiles.Count > 0)
        {
            _engine.SetProfile(_profiles[0].Profile);
            ProfileCombo.SelectedIndex = 0;
        }

        _engine.Start();
        _isRunning = true;
        ToggleButton.Content = "⏹ 停止引擎";
        ToggleButton.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        Log("▶ 引擎已启动");
    }

    private void StopEngine()
    {
        _engine.Stop();
        _isRunning = false;
        ToggleButton.Content = "▶ 启动引擎";
        ToggleButton.Background = (Brush)FindResource("Accent");
        Log("⏹ 引擎已停止");
        SetStatus("引擎已停止");
    }

    private void ScanController()
    {
        var devices = FlyDigiDevice.ScanDevices();
        if (devices.Length > 0)
        {
            foreach (var d in devices)
                Log($"\U0001F4E1 HID接口: {d.ProductName} PID=0x{d.ProductId:X4}");

            var known = devices.FirstOrDefault(d => d.IsKnown);
            if (known != null)
                Log($"\U0001F4E1 检测到飞智手柄: {known.ProductName} (PID=0x{known.ProductId:X4})");

            bool ok = _device.Connect();
            if (ok)
                Log($"✅ 手柄连接成功 (PID=0x{_device.ProductId:X4})");
            else
                Log("⚠ 手柄连接失败，请确认已通过 USB 连接");
        }
        else
            Log("\U0001F50D 未检测到飞智手柄");
    }

    private void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Clear();
        ProfileCombo.Items.Clear();

        var loaded = TriggerProfile.LoadAll(_profilesDir);
        foreach (var (path, profile) in loaded)
        {
            _profiles.Add((path, profile));
            ProfileCombo.Items.Add(profile);
        }

        if (loaded.Count > 0)
        {
            ProfileCombo.SelectedIndex = 0;
            Log($"\U0001F4C2 已加载 {loaded.Count} 个配置文件");
        }
        else
            Log("⚠ 未找到配置文件");
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is TriggerProfile profile)
        {
            ProfileDesc.Text = $"{profile.Description}\n版本: {profile.Version} · 规则数: {profile.Rules.Count}";
            if (_isRunning)
                _engine.SetProfile(profile);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        _logCount = 0;
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }

        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogList.Items.Insert(0, entry);
        _logCount++;

        while (LogList.Items.Count > 200)
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    private void SetStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(status));
            return;
        }
        StatusText.Text = status;
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add src/StellarForceAdapt/MainWindow.xaml.cs
git commit -m "$(cat <<'EOF'
refactor: remove debug display, console attach, test buttons, file logging from MainWindow

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Update profile JSON to new XInput format

**Files:**
- Delete: `src/StellarForceAdapt/Profiles/controller_mapping.json`
- Overwrite: `src/StellarForceAdapt/Profiles/stellar_blade.json`

- [ ] **Step 1: Remove old controller mapping**

```bash
rm src/StellarForceAdapt/Profiles/controller_mapping.json
```

- [ ] **Step 2: Write example generic profile**

Write `src/StellarForceAdapt/Profiles/example.json`:

```json
{
  "name": "示例配置",
  "version": "1.0",
  "description": "通用示例 — RT按下时机枪反馈，LT按下时赛车阻尼",
  "rules": [
    {
      "id": "rt_mg",
      "name": "RT 机枪反馈",
      "priority": 100,
      "cooldown_ms": 0,
      "condition": {
        "buttons": 0,
        "buttons_any": 0,
        "left_trigger_min": 0,
        "left_trigger_max": 255,
        "right_trigger_min": 30,
        "right_trigger_max": 255,
        "left_stick_magnitude_min": 0,
        "right_stick_magnitude_min": 0
      },
      "effect": {
        "type": "force_adapt",
        "mode": "machinegun",
        "target": "right",
        "duration_ms": 0
      }
    },
    {
      "id": "lt_racing",
      "name": "LT 赛车阻尼",
      "priority": 100,
      "cooldown_ms": 0,
      "condition": {
        "buttons": 0,
        "buttons_any": 0,
        "left_trigger_min": 30,
        "left_trigger_max": 255,
        "right_trigger_min": 0,
        "right_trigger_max": 255,
        "left_stick_magnitude_min": 0,
        "right_stick_magnitude_min": 0
      },
      "effect": {
        "type": "force_adapt",
        "mode": "racing",
        "target": "left",
        "duration_ms": 0
      }
    }
  ]
}
```

- [ ] **Step 3: Remove old stellar_blade.json**

```bash
rm src/StellarForceAdapt/Profiles/stellar_blade.json
```

- [ ] **Step 4: Commit**

```bash
git add src/StellarForceAdapt/Profiles/
git commit -m "$(cat <<'EOF'
refactor: replace game-specific profiles with generic XInput example profile

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Build and fix compilation errors

**Files:** All modified

- [ ] **Step 1: Build the project**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

Expected: Build succeeds with 0 errors.

- [ ] **Step 2: If build fails, fix errors and re-run**

Check error output. Expected potential issues:
- Missing imports in MappingEngine.cs (add `using StellarForceAdapt.Monitor;` if needed)
- ControllerMapping references in MainWindow.xaml.cs (should be gone)
- `GameStateUpdate` event references (should be gone)

Fix any compilation errors, then re-run build until it passes.

---

### Task 10: Write README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Overwrite README.md**

```markdown
# StellarForceAdapt

飞智 (FlyDigi) 手柄 ForceAdapt 自适应扳机通用适配器。

通过 USB HID 直连飞智手柄，下发 ForceAdapt 命令实现 6 种扳机力反馈模式，通过 JSON 配置文件驱动，基于 XInput 输入实时响应。

## 功能

- **6 种力反馈模式**：Off / Racing / Machinegun / Sniper / TriggerLock / Vibration
- **XInput 输入捕获**：双扳机 + 按键 + 摇杆，~250Hz 轮询
- **JSON 配置驱动**：优先级排序、冷却时间、条件组合
- **左右扳机独立控制**：各自独立的模式和状态追踪

## 支持手柄

- 飞智八爪鱼5 (APEX 5, PID 0x2501)
- 飞智 Vader 4 Pro (PID 0x2012)
- 飞智 APEX 4 (PID 0x2021/0x2023)
- 飞智 Vader 3 Pro (PID 0x2011)

## 快速开始

1. 下载 [Releases](../../releases) 中的最新版本
2. 解压到任意目录
3. 通过 USB 连接飞智手柄
4. 运行 `StellarForceAdapt.exe`
5. 选择配置文件，点击"启动引擎"

## 配置文件

扳机效果规则位于 `Profiles/` 目录，JSON 格式：

```jsonc
{
  "name": "示例配置",
  "version": "1.0",
  "description": "RT按下时机枪反馈",
  "rules": [
    {
      "id": "rt_mg",
      "name": "RT 机枪反馈",
      "priority": 100,
      "cooldown_ms": 0,
      "condition": {
        "buttons": 0,              // XInput 按键掩码 (必须全部按下, 0=不检查)
        "buttons_any": 0,          // 任意按下即触发 (0=不检查)
        "left_trigger_min": 0,     // LT 下限 (0-255)
        "left_trigger_max": 255,   // LT 上限
        "right_trigger_min": 30,   // RT 下限
        "right_trigger_max": 255,  // RT 上限
        "left_stick_magnitude_min": 0,  // 左摇杆幅度下限 (0-32768)
        "right_stick_magnitude_min": 0  // 右摇杆幅度下限
      },
      "effect": {
        "type": "force_adapt",
        "mode": "machinegun",     // off / racing / machinegun / sniper / triggerlock / vibrate
        "target": "right",        // left / right / both
        "duration_ms": 0,         // 0 = 持续
        "intensity": 220,
        "speed": 100
      }
    }
  ]
}
```

### XInput 按键掩码参考

| 按键 | 掩码 | | 按键 | 掩码 |
|------|------|-|------|------|
| A | 0x1000 | | LB | 0x0100 |
| B | 0x2000 | | RB | 0x0200 |
| X | 0x4000 | | Start | 0x0010 |
| Y | 0x8000 | | Back | 0x0020 |
| D-Up | 0x0001 | | L3 | 0x0040 |
| D-Down | 0x0002 | | R3 | 0x0080 |
| D-Left | 0x0004 | | | |
| D-Right | 0x0008 | | | |

## 构建

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

要求：.NET 9.0 SDK, Windows SDK (XInput)

## 协议

基于 SpaceStation 私有 HID 协议逆向。ForceAdapt 协议通过 Report ID 0x03 + Magic 0x5AA5 下发。

## License

MIT
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "$(cat <<'EOF'
docs: rewrite README for user-facing release

Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>
EOF
)"
```

---

### Task 11: Final build verification and push

**Files:** All

- [ ] **Step 1: Final clean build**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

Expected: Build succeeded, 0 Error(s).

- [ ] **Step 2: Push to GitHub**

```bash
git push -u origin release/user-facing
```

Expected: branch pushed, URL shown.

- [ ] **Step 3: Verify all commits on branch**

```bash
git log --oneline main..release/user-facing
```

Expected: Shows all commits from this plan, clean history.
