using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StellarForceAdapt.Monitor;

/// <summary>
/// Monitors Stellar Blade game process and reads game state from Cheat Engine
/// (primary, via CeDataSource) with XInput fallback.
/// </summary>
public class StellarBladeMonitor : IDisposable
{
    private Thread? _monitorThread;
    private CancellationTokenSource? _cts;
    private Process? _gameProcess;
    private nint _processHandle;
    private readonly CeDataSource _ceSource = new();
    private readonly object _stateLock = new();
    // Possible process names for Stellar Blade
    private static readonly string[] s_processNames =
        ["StellarBlade", "StellarBlade-Win64-Shipping", "SB"];

    public bool IsGameRunning => _gameProcess?.HasExited == false;
    public bool IsMonitoring => _monitorThread?.IsAlive == true;
    public bool IsCeConnected => _ceSource.IsConnected;

    public event EventHandler<GameState>? GameStateChanged;
    public event EventHandler<bool>? GameProcessChanged;

    public GameState CurrentState { get; private set; } = new();

    public void Start()
    {
        _ceSource.StateReceived += OnCeStateReceived;
        _ceSource.Start();

        _cts = new CancellationTokenSource();
        _monitorThread = new Thread(MonitorLoop)
        {
            IsBackground = true,
            Name = "Game-Monitor"
        };
        _monitorThread.Start();
    }

    public void Stop()
    {
        _ceSource.StateReceived -= OnCeStateReceived;
        _ceSource.Stop();

        _cts?.Cancel();
        _monitorThread = null;
        CloseProcessHandle();
    }

    private void OnCeStateReceived(object? sender, CeGameState ce)
    {
        lock (_stateLock)
        {
            var state = CurrentState;
            state.DetectionSource = DetectionSource.CE;
            state.Timestamp = DateTime.UtcNow;
            state.Health = ce.Health;
            state.MaxHealth = ce.MaxHealth;
            state.BetaEnergy = ce.BetaEnergy;
            state.MaxBetaEnergy = ce.MaxBetaEnergy;
            state.BurstEnergy = ce.BurstEnergy;
            state.MaxBurstEnergy = ce.MaxBurstEnergy;
            state.TachyEnergy = ce.TachyEnergy;
            state.MaxTachyEnergy = ce.MaxTachyEnergy;
        }
    }

    /// <summary>
    /// Update game state based on XInput data.
    /// Merges with CE data — only overwrites button/trigger fields, preserves CE values.
    /// XInput button flags: A=0x1000, B=0x2000, X=0x4000, Y=0x8000,
    /// LB=0x0100, RB=0x0200, L3=0x0040, R3=0x0080, Start=0x0010, Back=0x0020
    /// </summary>
    public void UpdateFromXInput(XInputState xinput)
    {
        lock (_stateLock)
        {
            var state = CurrentState;
            state.IsRunning = IsGameRunning;
            state.Timestamp = DateTime.UtcNow;

            // Only downgrade source if CE isn't connected
            if (!_ceSource.IsConnected)
            {
                state.DetectionSource = DetectionSource.XInput;
                state.Health = 0;
                state.MaxHealth = 0;
                state.BetaEnergy = 0;
                state.BurstEnergy = 0;
                state.TachyEnergy = 0;
            }

            bool attackPressed = (xinput.Buttons & 0x4000) != 0 || (xinput.Buttons & 0x8000) != 0; // X || Y
            bool blockPressed = (xinput.Buttons & 0x0100) != 0; // LB
            bool dodgePressed = (xinput.Buttons & 0x2000) != 0; // B
            bool shootPressed = xinput.RightTrigger > 30;
            bool aimPressed = xinput.LeftTrigger > 30;
            bool l3Pressed = (xinput.Buttons & 0x0040) != 0;
            int stickMag = Math.Max(Math.Abs(xinput.LeftThumbX), Math.Abs(xinput.LeftThumbY));

            if (aimPressed && shootPressed)
                state.PlayerAction = PlayerAction.AimingAndShooting;
            else if (aimPressed)
                state.PlayerAction = PlayerAction.Aiming;
            else if (attackPressed)
                state.PlayerAction = PlayerAction.MeleeAttack;
            else if (shootPressed)
                state.PlayerAction = PlayerAction.ShootingWeapon;
            else if (blockPressed)
                state.PlayerAction = PlayerAction.Blocking;
            else if (dodgePressed)
                state.PlayerAction = PlayerAction.Dodging;
            else if (l3Pressed || stickMag > 20000)
                state.PlayerAction = PlayerAction.Sprinting;
            else if (stickMag > 5000)
                state.PlayerAction = PlayerAction.Walking;
            else
                state.PlayerAction = PlayerAction.Idle;

            state.LeftTriggerPosition = xinput.LeftTrigger;
            state.RightTriggerPosition = xinput.RightTrigger;
            state.InCombat = attackPressed || blockPressed || shootPressed;

            GameStateChanged?.Invoke(this, state);
        }
    }

    public void Dispose()
    {
        Stop();
        _ceSource.Dispose();
    }

    private void MonitorLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var wasRunning = IsGameRunning;
                FindGameProcess();
                var nowRunning = IsGameRunning;

                if (wasRunning != nowRunning)
                {
                    Debug.WriteLine($"[Game] Stellar Blade {(nowRunning ? "detected" : "exited")}");
                    GameProcessChanged?.Invoke(this, nowRunning);

                    if (nowRunning)
                        OpenProcessHandle();
                    else
                        CloseProcessHandle();
                }

                Thread.Sleep(1000);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Game] Monitor error: {ex.Message}");
                Thread.Sleep(2000);
            }
        }
    }

    private void FindGameProcess()
    {
        foreach (var name in s_processNames)
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                _gameProcess = procs[0];
                return;
            }
        }
        _gameProcess = null;
    }

    private void OpenProcessHandle()
    {
        if (_gameProcess == null) return;

        try
        {
            _processHandle = OpenProcess(0x0010 | 0x0008, false, _gameProcess.Id);
            // 0x0010 = PROCESS_VM_READ, 0x0008 = PROCESS_QUERY_INFORMATION
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Game] Failed to open process handle: {ex.Message}");
            _processHandle = nint.Zero;
        }
    }

    private void CloseProcessHandle()
    {
        if (_processHandle != nint.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = nint.Zero;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint hObject);
}

// --- Game State Types ---

public enum PlayerAction
{
    Unknown,
    Idle,
    Walking,
    Sprinting,
    MeleeAttack,
    ShootingWeapon,
    Aiming,
    AimingAndShooting,
    Blocking,
    Dodging,
    UsingSkill,
    Interacting,
}

public enum DetectionSource
{
    Memory,
    XInput,
    CE,
}

public class GameState
{
    public bool IsRunning { get; set; }
    public DateTime Timestamp { get; set; }
    public DetectionSource DetectionSource { get; set; } = DetectionSource.XInput;

    // In-game state from CE/memory (floats — exact game values)
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public float BetaEnergy { get; set; }
    public float MaxBetaEnergy { get; set; }
    public float BurstEnergy { get; set; }
    public float MaxBurstEnergy { get; set; }
    public float TachyEnergy { get; set; }
    public float MaxTachyEnergy { get; set; }
    public int WeaponType { get; set; }
    public int ComboCount { get; set; }
    public bool InCombat { get; set; }
    public bool SkillReady { get; set; }

    // Derived
    public float HealthPercent => MaxHealth > 0 ? Health / MaxHealth : 1f;
    public bool TachyModeActive => TachyEnergy > 0;
    public bool BetaSkillAvailable => BetaEnergy > 0;
    public bool BurstSkillAvailable => BurstEnergy > 0;

    // Inferred state (from XInput)
    public PlayerAction PlayerAction { get; set; } = PlayerAction.Unknown;
    public byte LeftTriggerPosition { get; set; }
    public byte RightTriggerPosition { get; set; }

    public bool IsAttacking =>
        PlayerAction is PlayerAction.MeleeAttack or PlayerAction.ShootingWeapon;

    public bool IsAiming =>
        PlayerAction is PlayerAction.Aiming or PlayerAction.AimingAndShooting;

    public override string ToString()
    {
        if (!IsRunning) return "Game not detected";
        var sb = new System.Text.StringBuilder();
        sb.Append($"Action: {PlayerAction} | Combat: {InCombat} | Source: {DetectionSource}");
        if (DetectionSource >= DetectionSource.CE)
        {
            sb.Append($" | HP: {Health}/{MaxHealth}");
            sb.Append($" | Beta: {BetaEnergy}/{MaxBetaEnergy}");
            sb.Append($" | Tachy: {(TachyModeActive ? "ON" : "OFF")}");
        }
        sb.Append($" | Triggers: L={LeftTriggerPosition} R={RightTriggerPosition}");
        return sb.ToString();
    }
}
