using System.Diagnostics;
using System.Runtime.InteropServices;
using StellarForceAdapt.Mapping;

namespace StellarForceAdapt.Monitor;

/// <summary>
/// Monitors Stellar Blade game process and reads game state from memory.
/// Falls back to XInput-based state detection when memory reading isn't available.
/// </summary>
public class StellarBladeMonitor : IDisposable
{
    private Thread? _monitorThread;
    private CancellationTokenSource? _cts;
    private Process? _gameProcess;
    private nint _processHandle;

    // Possible process names for Stellar Blade
    private static readonly string[] s_processNames =
        ["StellarBlade", "StellarBlade-Win64-Shipping"];

    // Memory offsets (will need updating if game patches change these)
    // These are placeholder offsets - the real values need to be discovered
    private static class Offsets
    {
        // Player state structure offsets (relative to base address)
        public const int PlayerBase = 0x0;        // Will be resolved at runtime
        public const int WeaponType = 0x0;        // Current weapon type
        public const int CombatState = 0x0;       // In combat or not
        public const int Health = 0x0;            // Current health
        public const int MaxHealth = 0x0;         // Max health
        public const int IsAiming = 0x0;          // Is character aiming
        public const int IsSprinting = 0x0;       // Is sprinting
        public const int IsAttacking = 0x0;       // Currently attacking
        public const int ComboCount = 0x0;        // Current combo count
        public const int SkillReady = 0x0;        // Skill/Burst ready
        public const int MovementSpeed = 0x0;     // Current movement speed
    }

    public bool IsGameRunning => _gameProcess?.HasExited == false;
    public bool IsMonitoring => _monitorThread?.IsAlive == true;

    public event EventHandler<GameState>? GameStateChanged;
    public event EventHandler<bool>? GameProcessChanged;

    public GameState CurrentState { get; private set; } = new();

    public void Start()
    {
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
        _cts?.Cancel();
        _monitorThread = null;
        CloseProcessHandle();
    }

    /// <summary>
    /// Try to read game memory for detailed state.
    /// Returns null if memory reading fails.
    /// </summary>
    public GameState? TryReadMemoryState()
    {
        if (!IsGameRunning || _processHandle == nint.Zero)
            return null;

        try
        {
            // TODO: Implement actual memory reading once offsets are discovered
            // For now, return null to trigger XInput-based fallback
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Update game state based on HID gamepad data (primary detection method for FlyDigi).
    /// Stellar Blade PC default controls:
    ///   X/Y = melee attacks, LB = block, LT = aim, RT = shoot, B = dodge, A = jump
    /// </summary>
    public void UpdateFromHID(HIDGamepadState state, Mapping.ControllerMapping mapping)
    {
        var raw = state.Raw ?? [];
        var gameState = new GameState
        {
            IsRunning = IsGameRunning,
            Timestamp = DateTime.UtcNow,
            DetectionSource = DetectionSource.Hybrid,
        };

        bool attackPressed = mapping.IsPressed("X", raw) || mapping.IsPressed("Y", raw);
        bool blockPressed = mapping.IsPressed("LB", raw);
        bool dodgePressed = mapping.IsPressed("B", raw);
        bool shootPressed = state.RightTrigger > 30;
        bool aimPressed = state.LeftTrigger > 30;

        if (aimPressed && shootPressed)
            gameState.PlayerAction = PlayerAction.AimingAndShooting;
        else if (aimPressed)
            gameState.PlayerAction = PlayerAction.Aiming;
        else if (attackPressed)
            gameState.PlayerAction = PlayerAction.MeleeAttack;
        else if (shootPressed)
            gameState.PlayerAction = PlayerAction.ShootingWeapon;
        else if (blockPressed)
            gameState.PlayerAction = PlayerAction.Blocking;
        else if (dodgePressed)
            gameState.PlayerAction = PlayerAction.Dodging;
        else
            gameState.PlayerAction = PlayerAction.Idle;

        gameState.LeftTriggerPosition = state.LeftTrigger;
        gameState.RightTriggerPosition = state.RightTrigger;
        gameState.InCombat = attackPressed || blockPressed || shootPressed;

        CurrentState = gameState;
        GameStateChanged?.Invoke(this, gameState);
    }

    /// <summary>
    /// Update game state based on XInput data (fallback when HID isn't available).
    /// XInput button flags: A=0x1000, B=0x2000, X=0x4000, Y=0x8000,
    /// LB=0x0100, RB=0x0200, L3=0x0040, R3=0x0080, Start=0x0010, Back=0x0020
    /// </summary>
    public void UpdateFromXInput(XInputState xinput)
    {
        var state = new GameState
        {
            IsRunning = IsGameRunning,
            Timestamp = DateTime.UtcNow,
            DetectionSource = DetectionSource.XInput,
        };

        bool attackPressed = (xinput.Buttons & 0x4000) != 0 || (xinput.Buttons & 0x8000) != 0; // X || Y
        bool blockPressed = (xinput.Buttons & 0x0100) != 0; // LB
        bool dodgePressed = (xinput.Buttons & 0x2000) != 0; // B
        bool shootPressed = xinput.RightTrigger > 30;
        bool aimPressed = xinput.LeftTrigger > 30;

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
        else
            state.PlayerAction = PlayerAction.Idle;

        state.LeftTriggerPosition = xinput.LeftTrigger;
        state.RightTriggerPosition = xinput.RightTrigger;
        state.InCombat = attackPressed || blockPressed || shootPressed;

        CurrentState = state;
        GameStateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        Stop();
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

    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);
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
    Hybrid,
}

public class GameState
{
    public bool IsRunning { get; set; }
    public DateTime Timestamp { get; set; }
    public DetectionSource DetectionSource { get; set; } = DetectionSource.XInput;

    // In-game state (from memory reading when available)
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int WeaponType { get; set; }
    public int ComboCount { get; set; }
    public bool InCombat { get; set; }
    public bool SkillReady { get; set; }

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
        return $"Action: {PlayerAction} | Combat: {InCombat} | Triggers: L={LeftTriggerPosition} R={RightTriggerPosition} | Source: {DetectionSource}";
    }
}
