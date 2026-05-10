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
    private readonly HIDGamepadReader _gamepad;
    private readonly StellarBladeMonitor _gameMonitor;
    private readonly CancellationTokenSource _cts = new();

    private Thread? _engineThread;
    private List<RuleState> _activeRules = [];
    private bool _running;

    // ForceAdapt effect tracking — prevents the 200Hz loop from clearing active effects
    private ForceAdaptEffectState? _activeForceAdapt;
    private DateTime _forceAdaptExpiry = DateTime.MinValue;

    private struct ForceAdaptEffectState
    {
        public ForceAdaptProtocol.ForceAdaptMode Mode;
        public byte Position;
        public byte Intensity;
        public byte Speed;
        public byte Flags;
    }


    public TriggerProfile? CurrentProfile { get; private set; }
    public bool IsRunning => _running;
    public GameState CurrentGameState => _gameMonitor.CurrentState;

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<GameState>? GameStateUpdate;
    public event EventHandler<string>? EffectTriggered;

    public ControllerMapping? ButtonMapping { get; set; }

    public MappingEngine(FlyDigiDevice device, HIDGamepadReader gamepad, StellarBladeMonitor gameMonitor)
    {
        _device = device;
        _gamepad = gamepad;
        _gameMonitor = gameMonitor;

        _gamepad.StateChanged += OnGamepadStateChanged;
        _gameMonitor.GameStateChanged += OnGameStateChanged;
        _gameMonitor.GameProcessChanged += OnGameProcessChanged;
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

        if (!_gamepad.IsConnected) _gamepad.Connect();
        _gamepad.Start();
        _gameMonitor.Start();

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
        _gamepad.Stop();
        _gameMonitor.Stop();

        // Reset triggers
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
        _gamepad.StateChanged -= OnGamepadStateChanged;
        _gameMonitor.GameStateChanged -= OnGameStateChanged;
        _gameMonitor.GameProcessChanged -= OnGameProcessChanged;
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

        // Check if a ForceAdapt effect has expired
        if (_activeForceAdapt != null && DateTime.UtcNow >= _forceAdaptExpiry)
        {
            _device.SetForceAdaptEffect(ForceAdaptProtocol.ForceAdaptMode.Off, flags: _activeForceAdapt.Value.Flags);
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
                var mode = effect.Mode?.ToLower() switch
                {
                    "pushback" => ForceAdaptProtocol.ForceAdaptMode.Resistance,
                    "lock" => ForceAdaptProtocol.ForceAdaptMode.Resistance,
                    "vibrate" => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                    _ => ForceAdaptProtocol.ForceAdaptMode.Vibration,
                };

                byte flags = effect.Target switch
                {
                    TriggerTarget.Left => (byte)0x01,
                    TriggerTarget.Right => (byte)0x02,
                    TriggerTarget.Both => (byte)0x03,
                    _ => (byte)0x03,
                };

                _device.SetForceAdaptEffect(mode, effect.Position, effect.Intensity, effect.Speed, flags);

                // Track active ForceAdapt effect so EvaluateRules doesn't clear it
                _activeForceAdapt = new ForceAdaptEffectState
                {
                    Mode = mode,
                    Position = effect.Position,
                    Intensity = effect.Intensity,
                    Speed = effect.Speed,
                    Flags = flags,
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

    private void OnGamepadStateChanged(object? sender, HIDGamepadState state)
    {
        // Update game state from HID gamepad input
        if (!_gameMonitor.IsGameRunning || ButtonMapping == null) return;
        _gameMonitor.UpdateFromHID(state, ButtonMapping);
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
}
