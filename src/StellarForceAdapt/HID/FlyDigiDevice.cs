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

    public bool IsConnected => _stream?.CanWrite == true;
    public string? DeviceName => _device?.GetFriendlyName();
    public int? ProductId => _device?.ProductID;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? InputReportReceived;

    public bool Connect()
    {
        Disconnect();

        _device = FindFlyDigiDevice();
        if (_device == null) return false;

        try
        {
            _stream = _device.Open();
            _stream.ReadTimeout = Timeout.Infinite;

            _cts = new CancellationTokenSource();
            _watchdogThread = new Thread(WatchdogLoop) { IsBackground = true, Name = "HID-Watchdog" };
            _watchdogThread.Start();

            ConnectionChanged?.Invoke(this, true);
            Debug.WriteLine($"[HID] Connected to {DeviceName} (PID=0x{ProductId:X4})");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] Connection failed: {ex.Message}");
            _stream?.Dispose();
            _stream = null;
            _device = null;
            return false;
        }
    }

    public void Disconnect()
    {
        _connectionGen++;
        _cts?.Cancel();

        if (_stream != null)
        {
            var oldStream = _stream;
            _stream = null;
            ConnectionChanged?.Invoke(this, false);
            oldStream.Dispose();
        }
        _device = null;
        _watchdogThread = null;
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
    public bool SendReport(byte[] report)
    {
        if (_stream?.CanWrite != true) return false;
        int myGen = _connectionGen;

        try
        {
            int reportLen = _device?.GetMaxOutputReportLength() ?? ForceAdaptProtocol.OutputReportLength;
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

            _stream.Write(report, 0, report.Length);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] Write failed: {ex.Message}");
            if (_connectionGen == myGen)
                ConnectionChanged?.Invoke(this, false);
            return false;
        }
    }

    /// <summary>
    /// Send a 32-byte vendor protocol command (5aa5 format).
    /// Pads to the device's expected report length (e.g., 65 for CD2).
    /// </summary>
    public bool SendVendorCommand(byte[] vendorCmd32)
    {
        if (_stream?.CanWrite != true) return false;
        int myGen = _connectionGen;

        try
        {
            int reportLen = _device?.GetMaxOutputReportLength() ?? 65;
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
            _stream.Write(report, 0, report.Length);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] Vendor write failed: {ex.Message}");
            if (_connectionGen == myGen)
                ConnectionChanged?.Invoke(this, false);
            return false;
        }
    }

    /// <summary>
    /// V2 ForceAdapt entry point: apply a (side, mode) effect using byte-exact
    /// templates captured from SpaceStation. Handles non-vibration (6 packets)
    /// and vibration (7 packets with 0x52 haptic) transparently.
    /// </summary>
    public (bool ok, string details) ApplyTriggerEffect(
        ForceAdaptProtocol.TriggerSide side,
        ForceAdaptProtocol.ForceAdaptMode mode)
    {
        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay
            .BuildApplySequenceV2(side, mode);
        var names = ForceAdaptProtocol.VendorProtocol.CapturedReplay.V2PacketNames;

        var sb = new System.Text.StringBuilder();
        sb.Append('[').Append(side).Append(' ').Append(mode).Append("] ");
        bool allOk = true;
        for (int i = 0; i < seq.Length; i++)
        {
            bool ok = SendVendorCommand(seq[i]);
            sb.Append(ok ? "✓" : "✗").Append(names[i]).Append(' ');
            if (!ok) allOk = false;
            Thread.Sleep(12);
        }
        return (allOk, sb.ToString().TrimEnd());
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

    /// <summary>
    /// Reset both triggers to normal (no effect).
    /// </summary>
    public bool ResetTriggers()
    {
        ApplyTriggerEffectBoth(ForceAdaptProtocol.ForceAdaptMode.Off);
        return true;
    }

    /// <summary>
    /// Send rumble to both trigger motors.
    /// </summary>
    public bool SetTriggerRumble(byte left = 0, byte right = 0)
    {
        return SendReport(ForceAdaptProtocol.BuildRumbleCommand(
            leftTriggerRumble: left,
            rightTriggerRumble: right));
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
        var readBuffer = new byte[ForceAdaptProtocol.InputReportLength];
        int myGen = _connectionGen;

        if (_device?.ProductID == 0x6001)
        {
            while (!token.IsCancellationRequested && _stream != null)
            {
                try { token.WaitHandle.WaitOne(2000); }
                catch { break; }
            }
            return;
        }

        while (!token.IsCancellationRequested && _stream != null)
        {
            try
            {
                _stream.ReadTimeout = 200;
                int read = _stream.Read(readBuffer, 0, readBuffer.Length);
                if (read > 0 && _connectionGen == myGen)
                {
                    InputReportReceived?.Invoke(this, readBuffer.Take(read).ToArray());
                }
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
