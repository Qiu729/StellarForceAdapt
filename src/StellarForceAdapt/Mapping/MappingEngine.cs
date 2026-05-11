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

    // ForceAdapt effect tracking — prevents the 200Hz loop from clearing active effects.
    // Stores the last-applied V2 (side, mode) pair and its expiry so we can cleanly
    // revert to Off once the rule's duration elapses.
    private ForceAdaptEffectState? _activeForceAdapt;
    private DateTime _forceAdaptExpiry = DateTime.MinValue;

    private readonly XInputWatcher _xinput = new();

    private struct ForceAdaptEffectState
    {
        public ForceAdaptProtocol.ForceAdaptMode Mode;
        public ForceAdaptProtocol.TriggerSide? Side; // null = both triggers
    }


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
        _activeForceAdapt = null;

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
        if (!gameState.IsRunning) return;

        // Check if a ForceAdapt effect has expired — revert to Off on the
        // same channel(s) so the trigger returns to a neutral analog axis.
        if (_activeForceAdapt != null && DateTime.UtcNow >= _forceAdaptExpiry)
        {
            var prev = _activeForceAdapt.Value;
            if (prev.Side.HasValue)
                _device.ApplyTriggerEffect(prev.Side.Value, ForceAdaptProtocol.ForceAdaptMode.Off);
            else
                _device.ApplyTriggerEffectBoth(ForceAdaptProtocol.ForceAdaptMode.Off);
            _activeForceAdapt = null;
        }

        // Find the highest-priority matching rule
        MappingRule? bestRule = null;
        int bestPriority = int.MinValue;

        foreach (var ruleState in _activeRules)
        {
            if (!ruleState.CanTrigger()) continue;

            if (EvaluateCondition(ruleState.Rule.Condition, gameState))
            {
                if (ruleState.Rule.Priority > bestPriority)
                {
                    bestPriority = ruleState.Rule.Priority;
                    bestRule = ruleState.Rule;
                }
            }
        }

        // Apply the matched effect
        if (bestRule != null)
        {
            ApplyEffect(bestRule.Effect);
            var ruleState = _activeRules.Find(r => r.Rule.Id == bestRule.Id);
            ruleState?.Triggered();
            EffectTriggered?.Invoke(this, bestRule.Name);
        }
        else if (_activeForceAdapt != null)
        {
            // ForceAdapt effect is still active — don't send default rumble that might interfere
        }
        else if (gameState.InCombat)
        {
            // Default subtle vibration when in combat
            _device.SetTriggerRumble(20, 20);
        }
        else
        {
            // No combat - light or no feedback
            _device.SetTriggerRumble(0, 0);
        }
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

        return true;
    }

    private void ApplyEffect(TriggerEffect effect)
    {
        if (!_device.IsConnected) return;

        switch (effect.Type)
        {
            case EffectType.ForceAdapt:
            {
                // V2 (2026-05): map the profile's free-form mode string onto
                // one of the six firmware modes. Unknown strings default to
                // Vibration so profiles written against the old V1 enum
                // (which only had Off/Resistance/Vibration) still behave
                // sensibly.
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

                // Route to LT / RT / Both based on the rule's Target field.
                ForceAdaptProtocol.TriggerSide? side = effect.Target switch
                {
                    TriggerTarget.Left  => ForceAdaptProtocol.TriggerSide.LT,
                    TriggerTarget.Right => ForceAdaptProtocol.TriggerSide.RT,
                    _                   => null, // Both
                };

                if (side.HasValue)
                    _device.ApplyTriggerEffect(side.Value, mode);
                else
                    _device.ApplyTriggerEffectBoth(mode);

                // Remember the active effect so the 200Hz loop doesn't stomp
                // it with default rumble, and so it can be cleanly reverted.
                _activeForceAdapt = new ForceAdaptEffectState
                {
                    Mode = mode,
                    Side = side,
                };
                _forceAdaptExpiry = effect.DurationMs > 0
                    ? DateTime.UtcNow.AddMilliseconds(effect.DurationMs)
                    : DateTime.MaxValue;
                break;
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
                break;
            }

            case EffectType.Sequence:
            {
                if (effect.Sequence != null)
                {
                    _ = PlaySequence(effect.Sequence);
                }
                break;
            }
        }
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
