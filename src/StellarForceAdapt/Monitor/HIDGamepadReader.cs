using System.Diagnostics;
using HidSharp;

namespace StellarForceAdapt.Monitor;

/// <summary>
/// Reads raw 15-byte HID reports from the ig_01 gamepad interface.
/// Button decoding is done externally via ControllerMapping.
/// Analog values (sticks, triggers) are decoded here.
/// </summary>
public class HIDGamepadReader : IDisposable
{
    private Thread? _pollThread;
    private CancellationTokenSource? _cts;
    private HidStream? _stream;
    private bool _running;

    private const int VendorId = 0x37D7;
    private const int ReportLength = 15;

    public event EventHandler<HIDGamepadState>? StateChanged;

    public HIDGamepadState CurrentState { get; private set; }
    public bool IsConnected => _stream?.CanRead == true;
    public bool IsRunning => _running;

    /// <summary>Diagnostic log callback for UI integration (set by MainWindow).</summary>
    public static Action<string>? Log;

    public bool Connect()
    {
        Disconnect();

        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == VendorId).ToArray();

        // 1) Precise ig_01 match
        var device = devices.FirstOrDefault(d => d.DevicePath.Contains("ig_01"));

        // 2) Non-CD2 (PID != 0x6001) with exactly 15-byte input report
        device ??= devices.FirstOrDefault(d =>
            d.ProductID != 0x6001 && d.GetMaxInputReportLength() == ReportLength);

        if (device == null) return false;
        Log?.Invoke($"📡 HID 输入接口: {device.DevicePath}");

        try
        {
            _stream = device.Open();
            Debug.WriteLine("[HIDGamepad] Connected");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HIDGamepad] Connection failed: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Start(int pollIntervalMs = 4)
    {
        if (_running) return;
        if (_stream == null && !Connect()) return;

        _running = true;
        _cts = new CancellationTokenSource();
        _pollThread = new Thread(() => PollLoop(pollIntervalMs))
        {
            IsBackground = true,
            Name = "HIDGamepad-Poll"
        };
        _pollThread.Start();
        Debug.WriteLine("[HIDGamepad] Started");
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _pollThread = null;
        Debug.WriteLine("[HIDGamepad] Stopped");
    }

    public void Dispose()
    {
        Stop();
        Disconnect();
        _cts?.Dispose();
    }

    /// <summary>
    /// Capture idle state for delta detection during binding.
    /// Reads until no changes for 200ms.
    /// </summary>
    public byte[] CaptureIdle()
    {
        byte[]? prev = null;
        int stableCount = 0;
        var buf = new byte[ReportLength];

        // Try up to 100 reads (about 500ms at 5ms each)
        for (int i = 0; i < 100 && _stream != null; i++)
        {
            try
            {
                _stream.ReadTimeout = 5;
                int read = _stream.Read(buf, 0, ReportLength);
                if (read == ReportLength)
                {
                    if (prev != null && buf.AsSpan(0, ReportLength).SequenceEqual(prev))
                        stableCount++;
                    else
                        stableCount = 0;

                    if (stableCount >= 5) // stable for 5 consecutive reads
                        return (byte[])buf.Clone();

                    prev = (byte[])buf.Clone();
                }
            }
            catch (TimeoutException) { }
        }

        return prev ?? new byte[ReportLength];
    }

    private void PollLoop(int intervalMs)
    {
        var buf = new byte[ReportLength];
        var token = _cts?.Token ?? CancellationToken.None;

        while (!token.IsCancellationRequested && _running)
        {
            try
            {
                if (_stream == null) { Thread.Sleep(100); continue; }

                _stream.ReadTimeout = intervalMs;
                int read = _stream.Read(buf, 0, ReportLength);

                if (read >= ReportLength)
                {
                    var state = DecodeAnalog(buf);
                    CurrentState = state;
                    StateChanged?.Invoke(this, state);
                }

                Thread.Sleep(intervalMs);
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HIDGamepad] Error: {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Decode analog values from the 15-byte HID report.
    /// Does NOT decode buttons — that's done via ControllerMapping.
    /// Byte layout (from reverse engineering):
    ///   B0-B1:   Button bitmask (low/high)
    ///   B2-B3:   Left stick X (signed 16-bit LE)
    ///   B4-B5:   Left stick Y (signed 16-bit LE)
    ///   B6-B7:   Right stick X (signed 16-bit LE)
    ///   B8-B9:   Right stick Y (signed 16-bit LE)
    ///   B10:     Combined trigger value (128=rest, >128=left, <128=right)
    ///   B11:     Face/shoulder buttons
    ///   B12-B14: Padding/unknown
    /// </summary>
    private static HIDGamepadState DecodeAnalog(byte[] buf)
    {
        byte triggerRaw = buf[10];
        // B10: 128=rest, >128=left trigger, <128=right trigger
        byte leftTrigger = triggerRaw > 128 ? (byte)Math.Min((triggerRaw - 128) * 2, 255) : (byte)0;
        byte rightTrigger = triggerRaw < 128 ? (byte)Math.Min((128 - triggerRaw) * 2, 255) : (byte)0;

        return new HIDGamepadState
        {
            Connected = true,
            Raw = (byte[])buf.Clone(),
            LeftTrigger = leftTrigger,
            RightTrigger = rightTrigger,
            LeftThumbX = (short)(buf[2] | (buf[3] << 8)),
            LeftThumbY = (short)(buf[4] | (buf[5] << 8)),
            RightThumbX = (short)(buf[6] | (buf[7] << 8)),
            RightThumbY = (short)(buf[8] | (buf[9] << 8)),
        };
    }
}

public struct HIDGamepadState
{
    public bool Connected;
    /// <summary>Full 15-byte raw HID report.</summary>
    public byte[] Raw;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short LeftThumbX;
    public short LeftThumbY;
    public short RightThumbX;
    public short RightThumbY;
}
