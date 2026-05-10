using System.Diagnostics;
using System.Runtime.InteropServices;
using HidSharp;

namespace StellarForceAdapt.HID;

/// <summary>
/// Manages HID communication with FlyDigi controllers (八爪鱼5 / Apex 5).
/// </summary>
public class FlyDigiDevice : IDisposable
{
    private HidDevice? _device;
    private HidStream? _stream;
    private Thread? _watchdogThread;
    private CancellationTokenSource? _cts;
    private int _connectionGen; // incremented on Disconnect to invalidate old watchdog

    public bool IsConnected => _stream?.CanWrite == true;
    public string? DeviceName => _device?.GetFriendlyName();
    public int? ProductId => _device?.ProductID;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<byte[]>? InputReportReceived;

    /// <summary>
    /// Find and connect to a FlyDigi controller.
    /// Returns true if connected successfully.
    /// </summary>
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

    /// <summary>
    /// Disconnect from the device.
    /// </summary>
    public void Disconnect()
    {
        _connectionGen++; // invalidate any active watchdog
        _cts?.Cancel();

        if (_stream != null)
        {
            var oldStream = _stream;
            _stream = null; // clear first so watchdog/send see null
            ConnectionChanged?.Invoke(this, false);
            oldStream.Dispose(); // dispose last (unblocks watchdog Read)
        }
        _device = null;
        _watchdogThread = null;
    }

    /// <summary>
    /// Send a raw HID output report to the controller via Write (Output report).
    /// Uses the device's actual output report length to match the connected interface.
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
                // Truncate to the device's expected length
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
    /// Send a vendor protocol command (32-byte 5aa5 format).
    /// If connected to CD2 (65-byte), pads to 65 bytes.
    /// If connected to wired config interface (32-byte), sends as-is.
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
                // Pad 32-byte vendor command to device's expected length (e.g., 65 for CD2)
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
    /// Send a complete 5aa5 vendor protocol ForceAdapt sequence.
    /// Returns true if all commands succeeded.
    /// </summary>
    public bool SendVendorForceAdapt(byte mode, byte intensity = 100, byte[]? customData = null)
    {
        var seq = ForceAdaptProtocol.VendorProtocol.BuildApplySequence(mode, intensity, customData);
        foreach (var cmd in seq)
        {
            if (!SendVendorCommand(cmd))
                return false;
            Thread.Sleep(10);
        }
        return true;
    }

    /// <summary>
    /// Replay a byte-exact captured SpaceStation A4/A5/A6 triplet for the given slot (1..4).
    /// All three packets (including captured checksum/CRC bytes) are sent verbatim.
    /// Returns (ok, details) with per-packet status for diagnostics.
    /// </summary>
    public (bool ok, string details) ReplayCapturedSequence(int slot)
    {
        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay.BuildSequence(slot);
        string[] names = ["A4 BEGIN", "A5 SET", "A6 END"];
        var sb = new System.Text.StringBuilder();
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
    /// Replay with 0x11 SET_STATUS prefix + configurable trigger-mapping bytes.
    /// Sends [0x11, A4, A5(with data[8]/data[9] overrides), A6].
    /// </summary>
    public (bool ok, string details) ReplayCapturedWithPrefix(
        int slot, byte? map8 = null, byte? map9 = null)
    {
        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay
            .BuildSequenceWithPrefix(slot, map8, map9);
        string[] names = ["11 STATUS", "A4 BEGIN", "A5 SET", "A6 END"];
        var sb = new System.Text.StringBuilder();
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
    /// Send a ForceAdapt command using the mi_02 32-byte format.
    /// Returns true if successful.
    /// </summary>
    public bool SendForceAdaptMi02(byte mode, byte position, byte intensity, byte speed, byte flags)
    {
        if (_stream?.CanWrite != true) return false;
        int myGen = _connectionGen;

        try
        {
            var report = new byte[32];
            report[0] = 0x06;
            report[1] = mode;
            report[2] = position;
            report[3] = intensity;
            report[4] = speed;
            report[5] = flags;
            _stream.Write(report, 0, 32);
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
    /// Send a Feature report via SetFeature (alternative to Output report).
    /// Some HID interfaces (like CD2) may require Feature reports instead of Output reports.
    /// </summary>
    public bool SendFeatureReport(byte[] report)
    {
        if (_stream?.CanWrite != true) return false;

        try
        {
            int reportLen = ForceAdaptProtocol.OutputReportLength;
            if (report.Length < reportLen)
            {
                var padded = new byte[reportLen];
                Array.Copy(report, padded, Math.Min(report.Length, reportLen));
                report = padded;
            }

            _stream.SetFeature(report);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] SetFeature failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Send a raw Output report to a specific HID device (for multi-interface testing).
    /// Used by diagnostic code to test different interfaces.
    /// </summary>
    public static bool SendReportTo(HidDevice device, byte[] report)
    {
        try
        {
            var stream = device.Open();
            int reportLen = device.GetMaxOutputReportLength();
            if (reportLen <= 0) reportLen = 65;
            if (report.Length < reportLen)
            {
                var padded = new byte[reportLen];
                Array.Copy(report, padded, Math.Min(report.Length, reportLen));
                report = padded;
            }
            stream.Write(report, 0, reportLen);
            stream.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Send a Feature report to a specific HID device (for multi-interface testing).
    /// </summary>
    public static bool SendFeatureReportTo(HidDevice device, byte[] report)
    {
        try
        {
            var stream = device.Open();
            int reportLen = device.GetMaxFeatureReportLength();
            if (reportLen <= 0) reportLen = 65;
            if (report.Length < reportLen)
            {
                var padded = new byte[reportLen];
                Array.Copy(report, padded, Math.Min(report.Length, reportLen));
                report = padded;
            }
            stream.SetFeature(report);
            stream.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Try to send a report to a specific FlyDigi HID interface by product ID and output length.
    /// Used for diagnostic testing of different interfaces (mi_02, etc.).
    /// </summary>
    public static bool SendReportToInterface(int productId, int outputLength, byte[] report)
    {
        var result = SendReportToInterfaceDebug(productId, outputLength, report);
        return result == "OK";
    }

    /// <summary>
    /// Try to send a report to a specific FlyDigi HID interface by product ID and output length.
    /// First tries HidSharp, then falls back to raw P/Invoke CreateFile + WriteFile.
    /// </summary>
    public static string SendReportToInterfaceDebug(int productId, int outputLength, byte[] report)
    {
        try
        {
            var device = DeviceList.Local
                .GetHidDevices()
                .FirstOrDefault(d => d.VendorID == ForceAdaptProtocol.VendorId
                                  && d.ProductID == productId
                                  && d.GetMaxOutputReportLength() == outputLength);
            if (device == null) return "设备未找到";

            // Method 1: Try HidSharp Open + Write
            try
            {
                var stream = device.Open();
                var buf = new byte[outputLength];
                Array.Copy(report, buf, Math.Min(report.Length, outputLength));
                stream.Write(buf, 0, outputLength);
                stream.Dispose();
                return "OK";
            }
            catch
            {
                // Fall through to P/Invoke methods
            }

            var buf2 = new byte[outputLength];
            Array.Copy(report, buf2, Math.Min(report.Length, outputLength));

            // Method 2: CreateFile + WriteFile with GENERIC_READ|GENERIC_WRITE
            nint h1 = CreateFile(device.DevicePath, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
            if (h1 != new nint(-1))
            {
                bool ok = WriteFile(h1, buf2, outputLength, out _, nint.Zero);
                int err = Marshal.GetLastWin32Error();
                CloseHandle(h1);
                if (ok) return "OK";
                if (err != 87) return $"WriteFile失败: 错误码={err}";
            }

            // Method 3: CreateFile with FILE_FLAG_OVERLAPPED
            nint h2 = CreateFile(device.DevicePath, 0xC0000000, 3, nint.Zero, 3, 0x40000000, nint.Zero);
            if (h2 != new nint(-1))
            {
                bool ok = WriteFile(h2, buf2, outputLength, out _, nint.Zero);
                int err = Marshal.GetLastWin32Error();
                CloseHandle(h2);
                if (ok) return "OK";
                if (err != 87) return $"WriteFile失败: 错误码={err}";
            }

            // Method 4: CreateFile + HidD_SetOutputReport
            nint h3 = CreateFile(device.DevicePath, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
            if (h3 != new nint(-1))
            {
                bool ok = HidD_SetOutputReport(h3, buf2, outputLength);
                int err = Marshal.GetLastWin32Error();
                CloseHandle(h3);
                if (ok) return "OK";
                return $"HidD_SetOutputReport失败: 错误码={err}";
            }

            return $"所有方法失败";
        }
        catch (Exception ex)
        {
            return $"异常: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Try to stop the FlyDigi Space Station Service so we can access the mi_02 interface.
    /// Requires admin privileges. Service name from WMIC: "Flydigi Space Station Service"
    /// </summary>
    public static bool StopSpaceStationService()
    {
        bool anySuccess = false;

        // Try stopping the service by various possible names
        string[] serviceNames = [
            "Flydigi Space Station Service",  // WMIC name
            "FlydigiSpaceStationService",     // likely service name (no spaces)
            "SpaceStationService",            // process name
            "FlySpaceStationService",         // alternative
            "FlyDigiService",                 // generic
        ];

        foreach (var name in serviceNames)
        {
            try
            {
                var psi = new ProcessStartInfo("sc", $"stop \"{name}\"")
                {
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                };
                var process = Process.Start(psi);
                process?.WaitForExit(5000);
                if (process?.ExitCode == 0) { anySuccess = true; break; }
            }
            catch { }
        }

        // Also try net stop with the WMIC name
        if (!anySuccess)
        {
            try
            {
                var psi = new ProcessStartInfo("net", "stop \"Flydigi Space Station Service\"")
                {
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                };
                var process = Process.Start(psi);
                process?.WaitForExit(5000);
                if (process?.ExitCode == 0) anySuccess = true;
            }
            catch { }
        }

        // Also try taskkill on SpaceStationService process
        try
        {
            foreach (var proc in Process.GetProcessesByName("SpaceStationService"))
            {
                proc.Kill();
                anySuccess = true;
            }
        }
        catch { }

        return anySuccess;
    }

    /// <summary>
    /// Try to open mi_02 interface (32-byte output) directly.
    /// This requires SpaceStationService to be stopped.
    /// No watchdog thread since we only write to this interface.
    /// </summary>
    public bool ConnectMi02()
    {
        var (ok, _) = ConnectMi02Debug();
        return ok;
    }

    public (bool Ok, string Error) ConnectMi02Debug()
    {
        var device = DeviceList.Local
            .GetHidDevices()
            .FirstOrDefault(d => d.VendorID == ForceAdaptProtocol.VendorId
                              && d.ProductID == 0x2501
                              && d.GetMaxOutputReportLength() == 32);
        if (device == null) return (false, "mi_02设备未找到");

        try
        {
            Disconnect();
            _device = device;
            _stream = device.Open();
            _stream.ReadTimeout = 200;

            ConnectionChanged?.Invoke(this, true);
            Debug.WriteLine($"[HID] Connected to mi_02 interface (write-only)");
            return (true, "");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HID] mi_02 connection failed: {ex.Message}");
            _stream?.Dispose();
            _stream = null;
            _device = null;
            return (false, $"Open失败: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // P/Invoke for direct HID access
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(nint hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(nint hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    /// <summary>
    /// Get all FlyDigi HID interfaces with their capabilities (for diagnostics).
    /// </summary>
    public static string GetInterfaceDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .ToArray();

        sb.AppendLine($"共找到 {devices.Length} 个 FlyDigi HID 接口:");
        foreach (var d in devices)
        {
            sb.AppendLine($"  PID=0x{d.ProductID:X4} 路径={d.DevicePath}");
            sb.AppendLine($"     输出报告长度={d.GetMaxOutputReportLength()} 输入报告长度={d.GetMaxInputReportLength()} 功能报告长度={d.GetMaxFeatureReportLength()}");
        }

        return sb.ToString();
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
    /// Send a ForceAdapt mechanical effect.
    /// Uses the 5aa5 vendor protocol (SpaceStation-compatible) when possible,
    /// falls back to the legacy Report 0x06 protocol.
    /// </summary>
    public bool SetForceAdaptEffect(
        ForceAdaptProtocol.ForceAdaptMode mode,
        byte position = 0,
        byte intensity = 0,
        byte speed = 0,
        byte flags = 3) // both triggers by default
    {
        // Map our ForceAdaptMode to the SpaceStation protocol mode byte
        byte vendorMode = mode switch
        {
            ForceAdaptProtocol.ForceAdaptMode.Off => 0,
            ForceAdaptProtocol.ForceAdaptMode.Resistance => 2, // SpaceStation mode 2 = weapon/resistance
            ForceAdaptProtocol.ForceAdaptMode.Vibration => 1,  // SpaceStation mode 1 = vibration
            _ => 0,
        };

        // Try the vendor protocol (5aa5 commands, single Report 0x03 path).
        // Known limitation: only LT reliably responds on current firmware;
        // RT requires the correct cmd set (pending USBPcap re-derivation).
        if (vendorMode > 0)
        {
            bool ok = SendVendorForceAdapt(vendorMode, intensity);
            if (ok)
            {
                Debug.WriteLine($"[FA] Vendor protocol OK (mode={vendorMode}, int={intensity})");
                return true;
            }
            Debug.WriteLine($"[FA] Vendor protocol failed, trying legacy...");
        }

        // Fallback: legacy Report 0x06 protocol
        return SendReport(ForceAdaptProtocol.BuildForceAdaptCommand(
            mode, position, intensity, speed, flags));
    }

    /// <summary>
    /// Reset triggers to normal (no effect).
    /// Uses vendor protocol for reset, falls back to legacy rumble-based reset.
    /// </summary>
    public bool ResetTriggers()
    {
        // Try vendor protocol reset (Report 0x03)
        bool ok = SendVendorCommand(
            ForceAdaptProtocol.VendorProtocol.BuildSetEffect(0, 0));
        if (ok) return true;

        // Fallback: legacy reset
        return SendReport(ForceAdaptProtocol.BuildRumbleCommand());
    }

    /// <summary>
    /// Attempt to reconnect if connection is lost.
    /// </summary>
    public bool TryReconnect()
    {
        Disconnect();
        return Connect();
    }

    public void Dispose()
    {
        ResetTriggers();
        Disconnect();
        _cts?.Dispose();
    }

    // --- Private helpers ---

    /// <summary>
    /// Find the FlyDigi HID interface that supports output commands.
    /// Priority: CD2 interface (0x6001) first — it works alongside SpaceStationService.
    /// For wired mode (0x2501): prefer 32B output (config interface, mi_02 col01).
    /// Skip 0B/2B/13B output interfaces (keyboard HID, write-only endpoints, etc).
    /// </summary>
    private static HidDevice? FindFlyDigiDevice()
    {
        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .OrderByDescending(d => Array.IndexOf(ForceAdaptProtocol.KnownProductIds, d.ProductID))
            .ToArray();

        // Prefer CD2 interface (verified writable alongside SpaceStationService)
        var cd2 = devices.FirstOrDefault(d => d.ProductID == 0x6001);
        if (cd2 != null) return cd2;

        // Wired mode: prefer 32B output (mi_02 col01, vendor protocol interface)
        var wiredConfig = devices.FirstOrDefault(d => d.GetMaxOutputReportLength() == 32);
        if (wiredConfig != null) return wiredConfig;

        // Fallback: any device with reasonable output length (skip 0/2/13)
        return devices.FirstOrDefault(d =>
        {
            int len = d.GetMaxOutputReportLength();
            return len >= 32; // only interfaces with 32+ byte output
        }) ?? devices.FirstOrDefault(); // last resort: any device
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

    private void WatchdogLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var readBuffer = new byte[ForceAdaptProtocol.InputReportLength];
        int myGen = _connectionGen;

        // CD2 doesn't send periodic input — watchdog just waits for stream disposal
        if (_device?.ProductID == 0x6001)
        {
            // Keep thread alive but don't block on Read — CD2 write path handles disconnects
            while (!token.IsCancellationRequested && _stream != null)
            {
                try { token.WaitHandle.WaitOne(2000); }
                catch { break; }
            }
            return;
        }

        // Original watchdog for non-CD2 (gamepad) interfaces
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
            catch (TimeoutException)
            {
                continue;
            }
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

