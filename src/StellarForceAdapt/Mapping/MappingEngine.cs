using System.Diagnostics;
using StellarForceAdapt.HID;
using StellarForceAdapt.Monitor;

namespace StellarForceAdapt.Mapping;

/// <summary>
/// Core engine: monitors game state, evaluates profiles, sends trigger commands.
/// </summary>
public class MappingEngine : IDisposable
{
    private readonly FlyDigiDevice _device;
    private readonly StellarBladeMonitor _gameMonitor;
    private readonly CancellationTokenSource _cts = new();

    private Thread? _engineThread;
    private List<RuleState> _activeRules = [];
    private bool _running;

    // ForceAdapt effect tracking — per-side to prevent LT/RT from overwriting each other.
    // Each side independently tracks its active mode and expiry so the loop can cleanly
    // revert to Off when the duration elapses.
    private ForceAdaptProtocol.ForceAdaptMode? _activeLtMode;
    private ForceAdaptProtocol.ForceAdaptMode? _activeRtMode;
    private DateTime _ltExpiry = DateTime.MinValue;
    private DateTime _rtExpiry = DateTime.MinValue;

    private readonly XInputWatcher _xinput = new();


    public TriggerProfile? CurrentProfile { get; private set; }
    public bool IsRunning => _running;
    public XInputState CurrentInput => _xinput.CurrentState;
    public GameState CurrentGameState => _gameMonitor.CurrentState;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<GameState>? GameStateUpdate;
    public event EventHandler<string>? EffectTriggered;

    public MappingEngine(FlyDigiDevice device, StellarBladeMonitor gameMonitor)
    {
        _device = device;
        _gameMonitor = gameMonitor;

        _gameMonitor.GameStateChanged += OnGameStateChanged;
        _gameMonitor.GameProcessChanged += OnGameProcessChanged;
        _xinput.StateChanged += OnXInputStateChanged;
    }

    public void SetProfile(TriggerProfile profile)
    {
        CurrentProfile = profile;
        _activeRules = profile.Rules
            .OrderByDescending(r => r.Priority)
            .Select(r => new RuleState { Rule = r })
            .ToList();
        StatusChanged?.Invoke(this, $"Profile loaded: {profile.Name} ({profile.Game})");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        _xinput.Start(4);
        _gameMonitor.Start();

        _engineThread = new Thread(EngineLoop)
        {
            IsBackground = true,
            Name = "Mapping-Engine"
        };
        _engineThread.Start();

        StatusChanged?.Invoke(this, "XInput 输入已启用");
        StatusChanged?.Invoke(this, "Engine started");
        Debug.WriteLine("[Engine] Started");
    }

    public void Stop()
    {
        _running = false;
        _xinput.Stop();
        _gameMonitor.Stop();

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
        _gameMonitor.GameStateChanged -= OnGameStateChanged;
        _gameMonitor.GameProcessChanged -= OnGameProcessChanged;
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
                {
                    EvaluateRules();
                }
                Thread.Sleep(5); // ~200Hz evaluation loop
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
        var gameState = _gameMonitor.CurrentState;

        // When game exits, immediately turn off all active effects.
        if (!gameState.IsRunning)
        {
            if (_activeLtMode != null)
            {
                _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.LT, ForceAdaptProtocol.ForceAdaptMode.Off);
                _activeLtMode = null;
            }
            if (_activeRtMode != null)
            {
                _device.ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.RT, ForceAdaptProtocol.ForceAdaptMode.Off);
                _activeRtMode = null;
            }
            return;
        }

        // Safety net: expiry-based fallback (only fires when duration_ms > 0 and
        // ApplyEffect dedup hasn't refreshed it in time).
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

        // Evaluate each trigger side independently.
        var bestLt = FindBestRule(gameState, ForceAdaptProtocol.TriggerSide.LT);
        var bestRt = FindBestRule(gameState, ForceAdaptProtocol.TriggerSide.RT);

        // Primary turn-off: when a rule stops matching, revert the trigger to neutral.
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

        // RT applied first so its state is correctly set before LT's sequence runs.
        // MarkRuleTriggered only fires on actual HID application (not dedup refresh),
        // so cooldown_ms works correctly for one-shot effects.
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

    private MappingRule? FindBestRule(GameState gameState, ForceAdaptProtocol.TriggerSide side)
    {
        MappingRule? bestRule = null;
        int bestPriority = int.MinValue;

        foreach (var ruleState in _activeRules)
        {
            if (!ruleState.CanTrigger()) continue;
            if (!EvaluateCondition(ruleState.Rule.Condition, gameState)) continue;

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

    private bool EvaluateCondition(TriggerCondition condition, GameState state)
    {
        // Action check
        if (condition.Action != PlayerActionCondition.Any)
        {
            var matches = condition.Action switch
            {
                PlayerActionCondition.Idle => state.PlayerAction == PlayerAction.Idle,
                PlayerActionCondition.Moving => state.PlayerAction == PlayerAction.Walking,
                PlayerActionCondition.Sprinting => state.PlayerAction == PlayerAction.Sprinting,
                PlayerActionCondition.MeleeAttack => state.PlayerAction == PlayerAction.MeleeAttack,
                PlayerActionCondition.Shooting => state.PlayerAction == PlayerAction.ShootingWeapon,
                PlayerActionCondition.Aiming => state.PlayerAction == PlayerAction.Aiming,
                PlayerActionCondition.AimingAndShooting => state.PlayerAction == PlayerAction.AimingAndShooting,
                PlayerActionCondition.Blocking => state.PlayerAction == PlayerAction.Blocking,
                PlayerActionCondition.Dodging => state.PlayerAction == PlayerAction.Dodging,
                PlayerActionCondition.UsingSkill => state.PlayerAction == PlayerAction.UsingSkill,
                PlayerActionCondition.TachyMode => state.TachyModeActive,
                _ => false,
            };
            if (!matches) return false;
        }

        // Combat state check
        if (condition.InCombat.HasValue && state.InCombat != condition.InCombat.Value)
            return false;

        // Trigger position checks
        if (condition.TriggerMin.HasValue &&
            (state.RightTriggerPosition < condition.TriggerMin.Value &&
             state.LeftTriggerPosition < condition.TriggerMin.Value))
            return false;

        if (condition.TriggerMax.HasValue &&
            (state.RightTriggerPosition > condition.TriggerMax.Value &&
             state.LeftTriggerPosition > condition.TriggerMax.Value))
            return false;

        // Combo check
        if (condition.ComboMin.HasValue && state.ComboCount < condition.ComboMin.Value)
            return false;

        // CE-data conditions — only evaluate when CE data is available
        if (condition.HealthPercentMax.HasValue &&
            state.DetectionSource >= DetectionSource.CE &&
            state.HealthPercent > condition.HealthPercentMax.Value)
            return false;

        if (condition.BetaEnergyMin.HasValue &&
            state.DetectionSource >= DetectionSource.CE &&
            state.BetaEnergy < condition.BetaEnergyMin.Value)
            return false;

        if (condition.TachyActive.HasValue &&
            state.DetectionSource >= DetectionSource.CE &&
            state.TachyModeActive != condition.TachyActive.Value)
            return false;

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
                    "racing" or "pushback" or "damp" or "damping"
                                               => ForceAdaptProtocol.ForceAdaptMode.Racing,
                    "machinegun" or "burst" or "mg"
                                               => ForceAdaptProtocol.ForceAdaptMode.Machinegun,
                    "sniper" or "breakthrough" => ForceAdaptProtocol.ForceAdaptMode.Sniper,
                    "lock" or "triggerlock" or "resistance"
                                               => ForceAdaptProtocol.ForceAdaptMode.TriggerLock,
                    "vibrate" or "vibration" or "haptic"
                                               => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                    _                          => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                };

                ForceAdaptProtocol.TriggerSide? side = effect.Target switch
                {
                    TriggerTarget.Left  => ForceAdaptProtocol.TriggerSide.LT,
                    TriggerTarget.Right => ForceAdaptProtocol.TriggerSide.RT,
                    _                   => null, // Both
                };

                var expiry = effect.DurationMs > 0
                    ? DateTime.UtcNow.AddMilliseconds(effect.DurationMs)
                    : DateTime.MaxValue;

                // Per-side dedup: if same mode is already active on this side,
                // just refresh expiry instead of re-sending the full packet sequence.
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
                    FlyDigiDevice.Log?.Invoke($"🔧 引擎发送 [{side.Value} {mode}]: {(ok ? "OK" : "FAIL")} {details}");
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
                    FlyDigiDevice.Log?.Invoke($"🔧 引擎发送 [Both {mode}]: {(ok ? "OK" : "FAIL")} {details}");
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

                // Schedule auto-stop if duration is set
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
                {
                    _ = PlaySequence(effect.Sequence);
                }
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

    private void OnGameStateChanged(object? sender, GameState state)
    {
        GameStateUpdate?.Invoke(this, state);
    }

    private void OnGameProcessChanged(object? sender, bool running)
    {
        StatusChanged?.Invoke(this, running
            ? "Stellar Blade detected!"
            : "Waiting for Stellar Blade...");
    }

    private void OnXInputStateChanged(object? sender, XInputState state)
    {
        if (!_gameMonitor.IsGameRunning) return;
        _gameMonitor.UpdateFromXInput(state);
    }
}
