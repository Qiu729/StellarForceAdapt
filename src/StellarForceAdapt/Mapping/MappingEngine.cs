using System.Diagnostics;
using StellarForceAdapt.HID;
using StellarForceAdapt.Monitor;

namespace StellarForceAdapt.Mapping;

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
        if (cond.PreconditionButtons != 0 && (state.Buttons & cond.PreconditionButtons) != cond.PreconditionButtons)
            return false;

        if (cond.PreconditionLeftTrigger && state.LeftTrigger == 0)
            return false;

        if (cond.PreconditionRightTrigger && state.RightTrigger == 0)
            return false;

        if (cond.LeftTriggerMin > 0 || cond.LeftTriggerMax < 255)
        {
            if (state.LeftTrigger < cond.LeftTriggerMin || state.LeftTrigger > cond.LeftTriggerMax)
                return false;
        }

        if (cond.RightTriggerMin > 0 || cond.RightTriggerMax < 255)
        {
            if (state.RightTrigger < cond.RightTriggerMin || state.RightTrigger > cond.RightTriggerMax)
                return false;
        }

        if (cond.LeftStickMagnitudeMin > 0)
        {
            double mag = Math.Sqrt((long)state.LeftThumbX * state.LeftThumbX + (long)state.LeftThumbY * state.LeftThumbY);
            if (mag < cond.LeftStickMagnitudeMin) return false;
        }

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
        // XInputWatcher keeps CurrentState fresh.
    }
}
