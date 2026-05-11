using System.Diagnostics;
using HidSharp;

namespace StellarForceAdapt.HID;

public class FlyDigiDevice : IDisposable
{
    private HidDevice? _device;
    private HidStream? _stream;
    private Thread? _watchdogThread;
    private CancellationTokenSource? _cts;
    private int _connectionGen;

    // Serializes all HidStream access. HidStream is NOT thread-safe:
    // the WatchdogLoop reads, EngineLoop writes, and Disconnect swaps the stream.
    // Without this lock, concurrent Read/Write on the same stream causes
    // unpredictable failures that trigger the disconnect-reconnect cycle.
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private bool _rumbleReportedUnsupported;

    // Per-side effect state — preserves the other side when applying to one.
    private ForceAdaptProtocol.ForceAdaptMode _currentLtMode = ForceAdaptProtocol.ForceAdaptMode.Off;
    private ForceAdaptProtocol.ForceAdaptMode _currentRtMode = ForceAdaptProtocol.ForceAdaptMode.Off;
    private byte _ltP0, _ltP1, _ltP2, _ltP3, _ltP4;
    private byte _rtP0, _rtP1, _rtP2, _rtP3, _rtP4;

    public bool IsConnected => _stream?.CanWrite == true;
    public string? DeviceName => _device?.GetFriendlyName();
    public int? ProductId => _device?.ProductID;

    /// <summary>
    /// True when the connected HID interface supports standard output reports
    /// (ReportID 0x05) for rumble. Wired APEX5 (0x2501) uses a vendor-only
    /// 32-byte interface that only accepts ReportID 0x03 — rumble requires
    /// 65-byte output (CD2 wireless or gamepad interface).
    /// </summary>
    public bool RumbleSupported { get; private set; }

    /// <summary>Diagnostic log callback (set by MainWindow).</summary>
    public static Action<string>? Log;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? InputReportReceived;

    public bool Connect()
    {
        Disconnect();

        _device = FindFlyDigiDevice();
        if (_device == null) return false;

        try
        {
            var newStream = _device.Open();
            newStream.ReadTimeout = Timeout.Infinite;

            var newCts = new CancellationTokenSource();
            var newThread = new Thread(WatchdogLoop) { IsBackground = true, Name = "HID-Watchdog" };

            _streamLock.Wait();
            try
            {
                _stream = newStream;
                _cts = newCts;
                _watchdogThread = newThread;
            }
            finally { _streamLock.Release(); }

            // Rumble via ReportID 0x05 requires output report length >= 65.
            // Wired APEX5 (32-byte, vendor-only) doesn't support it.
            RumbleSupported = _device.GetMaxOutputReportLength() >= 65;

            newThread.Start();

            ConnectionChanged?.Invoke(this, true);
            Debug.WriteLine($"[HID] Connected to {DeviceName} (PID=0x{ProductId:X4})");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] Connection failed: {ex.Message}");
            _device = null;
            return false;
        }
    }

    public void Disconnect()
    {
        HidStream? oldStream;
        _streamLock.Wait();
        try
        {
            _connectionGen++;
            _cts?.Cancel();

            oldStream = _stream;
            _stream = null;
            _device = null;
            _watchdogThread = null;
        }
        finally { _streamLock.Release(); }

        RumbleSupported = false;

        if (oldStream != null)
        {
            ConnectionChanged?.Invoke(this, false);
            oldStream.Dispose();
        }
    }

    public bool TryReconnect()
    {
        Disconnect();
        return Connect();
    }

    /// <summary>
    /// Send a raw HID output report. Used for rumble commands.
    /// Auto-pads or trims to the device's output report length.
    /// </summary>
    public bool SendReport(byte[] report, bool reconnectOnFailure = true)
    {
        int myGen = 0;
        bool shouldNotify = false;
        _streamLock.Wait();
        try
        {
            var stream = _stream;
            var device = _device;
            if (stream?.CanWrite != true) return false;
            myGen = _connectionGen;

            int reportLen = device?.GetMaxOutputReportLength() ?? ForceAdaptProtocol.OutputReportLength;
            if (report.Length < reportLen)
            {
                var padded = new byte[reportLen];
                Array.Copy(report, padded, Math.Min(report.Length, reportLen));
                report = padded;
            }
            else if (report.Length > reportLen)
            {
                var trimmed = new byte[reportLen];
                Array.Copy(report, trimmed, reportLen);
                report = trimmed;
            }

            stream.Write(report, 0, report.Length);
            return true;
        }
        catch (Exception ex)
        {
            if (_connectionGen == myGen)
            {
                if (reconnectOnFailure)
                {
                    int outLen = _device?.GetMaxOutputReportLength() ?? 0;
                    byte rid = report.Length > 0 ? report[0] : (byte)0;
                    Log?.Invoke($"[HID] SendReport fail: {ex.GetType().Name}: {ex.Message}"
                        + $" | ReportID=0x{rid:X2} len={report.Length} devOutLen={outLen}");
                    // Null out the stream so IsConnected immediately returns false
                    // and the engine stops retrying on the dead handle.
                    _stream = null;
                    _device = null;
                    shouldNotify = true;
                }
                else if (!_rumbleReportedUnsupported)
                {
                    _rumbleReportedUnsupported = true;
                    Log?.Invoke($"[HID] Rumble not supported on this interface — "
                        + $"suppressing further reports (ReportID=0x{report[0]:X2})");
                }
            }
            return false;
        }
        finally
        {
            _streamLock.Release();
            if (shouldNotify)
                ConnectionChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Send a 32-byte vendor protocol command (5aa5 format).
    /// Pads to the device's expected report length (e.g., 65 for CD2).
    /// </summary>
    public bool SendVendorCommand(byte[] vendorCmd32)
    {
        int myGen = 0;
        bool shouldNotify = false;
        _streamLock.Wait();
        try
        {
            var stream = _stream;
            var device = _device;
            if (stream?.CanWrite != true) return false;
            myGen = _connectionGen;

            int reportLen = device?.GetMaxOutputReportLength() ?? 65;
            byte[] report;
            if (reportLen == 32)
            {
                report = vendorCmd32;
            }
            else
            {
                report = new byte[reportLen];
                Array.Copy(vendorCmd32, 0, report, 0, Math.Min(vendorCmd32.Length, reportLen));
            }
            stream.Write(report, 0, report.Length);
            return true;
        }
        catch (Exception ex)
        {
            byte rid = vendorCmd32.Length > 0 ? vendorCmd32[0] : (byte)0;
            int outLen = _device?.GetMaxOutputReportLength() ?? 0;
            Log?.Invoke($"[HID] VendorCmd fail: {ex.GetType().Name}: {ex.Message}"
                + $" | ReportID=0x{rid:X2} cmdLen={vendorCmd32.Length} devOutLen={outLen}");
            Debug.WriteLine($"[HID] Vendor write failed: {ex.Message}");
            if (_connectionGen == myGen)
            {
                _stream = null;
                _device = null;
                shouldNotify = true;
            }
            return false;
        }
        finally
        {
            _streamLock.Release();
            if (shouldNotify)
                ConnectionChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// V2 ForceAdapt entry point: apply a (side, mode) effect using byte-exact
    /// templates captured from SpaceStation. Preserves the other side's effect.
    /// </summary>
    public (bool ok, string details) ApplyTriggerEffect(
        ForceAdaptProtocol.TriggerSide side,
        ForceAdaptProtocol.ForceAdaptMode mode)
    {
        var (set23, end4) = ForceAdaptProtocol.VendorProtocol.CapturedReplay.GetTemplate(side, mode);
        var begin = ForceAdaptProtocol.VendorProtocol.CapturedReplay.GetBegin(side);

        byte p0 = set23[16], p1 = set23[17], p2 = set23[18], p3 = set23[19], p4 = set23[20];
        SaveSideState(side, mode, p0, p1, p2, p3, p4);

        byte[] ltSlot = BuildSlot(0x01, _currentLtMode, _ltP0, _ltP1, _ltP2, _ltP3, _ltP4);
        byte[] rtSlot = BuildSlot(0x02, _currentRtMode, _rtP0, _rtP1, _rtP2, _rtP3, _rtP4);
        byte[]? haptic = mode == ForceAdaptProtocol.ForceAdaptMode.Vibration
            ? BuildHaptic(side, set23) : null;

        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay
            .BuildApplySequenceV2(set23, end4, begin, ltSlot, rtSlot, haptic);
        return SendSequence(seq, $"[{side} {mode}]");
    }

    /// <summary>
    /// Apply the same effect to both triggers back-to-back.
    /// </summary>
    public (bool ok, string details) ApplyTriggerEffectBoth(ForceAdaptProtocol.ForceAdaptMode mode)
    {
        var (lOk, lDetails) = ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.LT, mode);
        Thread.Sleep(20);
        var (rOk, rDetails) = ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.RT, mode);
        return (lOk && rOk, lDetails + " | " + rDetails);
    }

    private static byte[] BuildSlot(byte sideMarker, ForceAdaptProtocol.ForceAdaptMode mode,
        byte p0, byte p1, byte p2, byte p3, byte p4)
    {
        if (mode == ForceAdaptProtocol.ForceAdaptMode.Vibration)
            return [0x01, sideMarker, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        return [0x01, sideMarker, (byte)mode, p0, p1, p2, p3, p4, 0x00, 0x00];
    }

    private static byte[] BuildHaptic(ForceAdaptProtocol.TriggerSide side, byte[] a5Payload)
    {
        byte sideByte = side == ForceAdaptProtocol.TriggerSide.LT ? (byte)0x01 : (byte)0x02;
        return [sideByte, 0x02,
            a5Payload[8], a5Payload[9], a5Payload[10], a5Payload[11],
            a5Payload[12], a5Payload[13], a5Payload[14], a5Payload[15], 0x00];
    }

    private void SaveSideState(ForceAdaptProtocol.TriggerSide side, ForceAdaptProtocol.ForceAdaptMode mode,
        byte p0, byte p1, byte p2, byte p3, byte p4)
    {
        if (side == ForceAdaptProtocol.TriggerSide.LT)
        {
            _currentLtMode = mode; _ltP0 = p0; _ltP1 = p1; _ltP2 = p2; _ltP3 = p3; _ltP4 = p4;
        }
        else
        {
            _currentRtMode = mode; _rtP0 = p0; _rtP1 = p1; _rtP2 = p2; _rtP3 = p3; _rtP4 = p4;
        }
    }

    private (bool ok, string details) SendSequence(byte[][] seq, string prefix)
    {
        var names = ForceAdaptProtocol.VendorProtocol.CapturedReplay.V2PacketNames;
        var sb = new System.Text.StringBuilder();
        sb.Append(prefix).Append(' ');
        bool allOk = true;
        for (int i = 0; i < seq.Length; i++)
        {
            bool ok = SendVendorCommand(seq[i]);
            sb.Append(ok ? '+' : '-').Append(names[i]).Append(' ');
            if (!ok) allOk = false;
            Thread.Sleep(12);
        }
        return (allOk, sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Reset both triggers to normal (no effect).
    /// </summary>
    public bool ResetTriggers()
    {
        ApplyTriggerEffectBoth(ForceAdaptProtocol.ForceAdaptMode.Off);
        return true;
    }

    /// <summary>
    /// Send rumble to both trigger motors (best-effort, no reconnection on failure).
    /// The wired 0x2501 vendor interface does not accept rumble ReportID 0x05
    /// (only vendor ReportID 0x03). Check RumbleSupported before calling.
    /// </summary>
    public bool SetTriggerRumble(byte left = 0, byte right = 0)
    {
        if (!RumbleSupported) return false;
        return SendReport(ForceAdaptProtocol.BuildRumbleCommand(
            leftTriggerRumble: left,
            rightTriggerRumble: right), reconnectOnFailure: false);
    }

    /// <summary>
    /// Get all detected FlyDigi devices (for UI display).
    /// </summary>
    public static FlyDigiDeviceInfo[] ScanDevices()
    {
        return DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .Select(d => new FlyDigiDeviceInfo
            {
                Path = d.DevicePath,
                ProductId = d.ProductID,
                VendorId = d.VendorID,
                ProductName = d.GetFriendlyName() ?? d.GetProductName() ?? "Unknown",
                IsKnown = ForceAdaptProtocol.KnownProductIds.Contains(d.ProductID),
            })
            .ToArray();
    }

    public void Dispose()
    {
        ResetTriggers();
        Disconnect();
        _cts?.Dispose();
        _streamLock.Dispose();
    }

    /// <summary>
    /// Find the FlyDigi HID interface that supports output commands.
    /// Priority: CD2 interface (0x6001) — works alongside SpaceStationService.
    /// </summary>
    private static HidDevice? FindFlyDigiDevice()
    {
        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .OrderByDescending(d => Array.IndexOf(ForceAdaptProtocol.KnownProductIds, d.ProductID))
            .ToArray();

        var cd2 = devices.FirstOrDefault(d => d.ProductID == 0x6001);
        if (cd2 != null) return cd2;

        var wiredConfig = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() == 32);
        if (wiredConfig != null) return wiredConfig;

        return devices.FirstOrDefault(d =>
        {
            int len = d.GetMaxOutputReportLength();
            return len >= 32;
        }) ?? devices.FirstOrDefault();
    }

    private void WatchdogLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        int myGen = _connectionGen;

        // CD2 (0x6001): read input reports for SpaceStation coexistence.
        // Use TryLock to avoid blocking engine writes on the same stream.
        if (_device?.ProductID == 0x6001)
        {
            var readBuffer = new byte[ForceAdaptProtocol.InputReportLength];
            while (!token.IsCancellationRequested)
            {
                HidStream? stream;
                _streamLock.Wait(token);
                try { stream = _stream; }
                finally { _streamLock.Release(); }
                if (stream == null) break;

                try
                {
                    stream.ReadTimeout = 200;
                    int read = stream.Read(readBuffer, 0, readBuffer.Length);
                    if (read > 0 && _connectionGen == myGen)
                        InputReportReceived?.Invoke(this, readBuffer.Take(read).ToArray());
                }
                catch (TimeoutException) { continue; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HID] Read error: {ex.Message}");
                    if (_connectionGen == myGen)
                        ConnectionChanged?.Invoke(this, false);
                    break;
                }
            }
            return;
        }

        // Non-CD2: no read loop (input handled by XInput).
        while (!token.IsCancellationRequested)
        {
            _streamLock.Wait(token);
            try { if (_stream == null) break; }
            finally { _streamLock.Release(); }

            try { token.WaitHandle.WaitOne(2000); }
            catch { break; }
        }
    }
}

public class FlyDigiDeviceInfo
{
    public string Path { get; set; } = "";
    public int ProductId { get; set; }
    public int VendorId { get; set; }
    public string ProductName { get; set; } = "";
    public bool IsKnown { get; set; }
}
