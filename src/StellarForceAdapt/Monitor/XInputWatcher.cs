using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StellarForceAdapt.Monitor;

/// <summary>
/// Monitors XInput controller state with high polling rate.
/// </summary>
public class XInputWatcher : IDisposable
{
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private XInputState _prevState;
    private bool _running;

    // Events
    public event EventHandler<XInputState>? StateChanged;
    public event EventHandler<XInputTriggerEventArgs>? TriggerChanged;
    public event EventHandler<XInputButtonEventArgs>? ButtonPressed;
    public event EventHandler<XInputButtonEventArgs>? ButtonReleased;

    /// <summary>Current controller state</summary>
    public XInputState CurrentState { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsRunning => _running;

    /// <summary>
    /// Start polling XInput at the specified interval.
    /// </summary>
    public void Start(int pollIntervalMs = 4) // ~250Hz
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _pollThread = new Thread(() => PollLoop(pollIntervalMs))
        {
            IsBackground = true,
            Name = "XInput-Poll"
        };
        _pollThread.Start();
        Debug.WriteLine("[XInput] Started monitoring");
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _pollThread = null;
        Debug.WriteLine("[XInput] Stopped monitoring");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private void PollLoop(int intervalMs)
    {
        var token = _cts?.Token ?? CancellationToken.None;

        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                var state = GetXInputState(0);
                IsConnected = state.Connected;

                if (state.Connected)
                {
                    DetectChanges(state);
                    CurrentState = state;
                }

                Thread.Sleep(intervalMs);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XInput] Error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    private void DetectChanges(XInputState newState)
    {
        var prev = _prevState;

        // Trigger position changes
        if (newState.LeftTrigger != prev.LeftTrigger)
            TriggerChanged?.Invoke(this, new XInputTriggerEventArgs(TriggerType.Left, newState.LeftTrigger));
        if (newState.RightTrigger != prev.RightTrigger)
            TriggerChanged?.Invoke(this, new XInputTriggerEventArgs(TriggerType.Right, newState.RightTrigger));

        // Button changes
        uint changed = newState.Buttons ^ prev.Buttons;
        if (changed != 0)
        {
            uint pressed = changed & newState.Buttons;
            uint released = changed & prev.Buttons;

            foreach (var kvp in s_buttonNames)
            {
                if ((pressed & kvp.Key) != 0)
                    ButtonPressed?.Invoke(this, new XInputButtonEventArgs(kvp.Value));
                if ((released & kvp.Key) != 0)
                    ButtonReleased?.Invoke(this, new XInputButtonEventArgs(kvp.Value));
            }
        }

        _prevState = newState;
        StateChanged?.Invoke(this, newState);
    }

    private static readonly Dictionary<uint, XInputButton> s_buttonNames = new()
    {
        [0x0001] = XInputButton.DPadUp,
        [0x0002] = XInputButton.DPadDown,
        [0x0004] = XInputButton.DPadLeft,
        [0x0008] = XInputButton.DPadRight,
        [0x0010] = XInputButton.Start,
        [0x0020] = XInputButton.Back,
        [0x0040] = XInputButton.LeftThumb,
        [0x0080] = XInputButton.RightThumb,
        [0x0100] = XInputButton.LeftShoulder,
        [0x0200] = XInputButton.RightShoulder,
        [0x1000] = XInputButton.A,
        [0x2000] = XInputButton.B,
        [0x4000] = XInputButton.X,
        [0x8000] = XInputButton.Y,
    };

    // --- XInput P/Invoke ---

    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int dwUserIndex, out XInputGamepadRaw pState);

    private static XInputState GetXInputState(int playerIndex)
    {
        int result = XInputGetState(playerIndex, out var raw);
        if (result != 0)
            return new XInputState { Connected = false };

        return new XInputState
        {
            Connected = true,
            Buttons = raw.wButtons,
            LeftTrigger = raw.bLeftTrigger,
            RightTrigger = raw.bRightTrigger,
            LeftThumbX = raw.sThumbLX,
            LeftThumbY = raw.sThumbLY,
            RightThumbX = raw.sThumbRX,
            RightThumbY = raw.sThumbRY,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepadRaw
    {
        public uint wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
        public uint dwPadding;
    }
}

// --- Data types ---

public struct XInputState
{
    public bool Connected;
    public uint Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftThumbX;
    public short LeftThumbY;
    public short RightThumbX;
    public short RightThumbY;
}

public enum XInputButton
{
    DPadUp, DPadDown, DPadLeft, DPadRight,
    Start, Back,
    LeftThumb, RightThumb,
    LeftShoulder, RightShoulder,
    A, B, X, Y,
}

public enum TriggerType { Left, Right }

public class XInputTriggerEventArgs(TriggerType trigger, byte value) : EventArgs
{
    public TriggerType Trigger { get; } = trigger;
    public byte Value { get; } = value;
}

public class XInputButtonEventArgs(XInputButton button) : EventArgs
{
    public XInputButton Button { get; } = button;
}
