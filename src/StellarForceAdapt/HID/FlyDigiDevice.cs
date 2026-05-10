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
    /// Send the complete 6-packet activation sequence captured from SpaceStation:
    /// 0x11 → A4 → A5 → A6 → 0x51(activate) → 0x51(finalize).
    /// The two 0x51 packets are the critical missing piece: without them the device
    /// only STORES the config but never APPLIES it to the physical trigger hardware,
    /// which explains why all previous replays got ACKs but produced zero haptic effect.
    /// </summary>
    public (bool ok, string details) ReplayFullActivation(
        int slot, byte? map8 = null, byte? map9 = null)
    {
        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay
            .BuildFullActivationSequence(slot, map8, map9);
        string[] names = ["11 STATUS", "A4 BEGIN", "A5 SET", "A6 END", "51 ACTIVATE", "51 FINALIZE"];
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
    /// V2 ForceAdapt entry point: apply a single (side, mode) effect to the
    /// physical trigger using byte-exact templates captured from SpaceStation.
    /// Handles the 6-packet non-vibration flush and the 7-packet 0x52 haptic
    /// path transparently. Returns (ok, diag) for UI/log surfaces.
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
    /// V2 convenience overload: apply the same (mode) effect to both triggers
    /// back-to-back. Used when a profile requests <c>TriggerTarget.Both</c>.
    /// </summary>
    public (bool ok, string details) ApplyTriggerEffectBoth(ForceAdaptProtocol.ForceAdaptMode mode)
    {
        var (lOk, lDetails) = ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.LT, mode);
        Thread.Sleep(20);
        var (rOk, rDetails) = ApplyTriggerEffect(ForceAdaptProtocol.TriggerSide.RT, mode);
        return (lOk && rOk, lDetails + " | " + rDetails);
    }

    /// <summary>
    /// Broadcast a Slot-2 A4/A5/A6 triplet to EVERY FlyDigi HID interface individually.
    /// Tries multiple send mechanisms per interface (HidSharp Write, Feature, HidD_SetOutputReport).
    /// Pauses 'interfaceDelayMs' between interfaces so the user can feel which one actually drives the triggers.
    /// Returns a multi-line report describing each attempt.
    /// </summary>
    public static string BroadcastSlot2ToAllInterfaces(int interfaceDelayMs, Action<string> perLineLog)
    {
        var sb = new System.Text.StringBuilder();
        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .OrderBy(d => d.ProductID)
            .ThenBy(d => d.GetMaxOutputReportLength())
            .ToArray();

        var seq = ForceAdaptProtocol.VendorProtocol.CapturedReplay.BuildSequence(2);

        void Log(string line) { sb.AppendLine(line); perLineLog?.Invoke(line); }

        Log($"🌐 枚举到 {devices.Length} 个 FlyDigi HID 接口，逐一广播 Slot 2 (间隔 {interfaceDelayMs}ms)");

        int idx = 0;
        foreach (var dev in devices)
        {
            idx++;
            int outLen = SafeInt(() => dev.GetMaxOutputReportLength());
            int inLen = SafeInt(() => dev.GetMaxInputReportLength());
            int featLen = SafeInt(() => dev.GetMaxFeatureReportLength());
            string shortPath = dev.DevicePath.Length > 48
                ? "…" + dev.DevicePath[^48..]
                : dev.DevicePath;
            Log($"── [{idx}/{devices.Length}] PID=0x{dev.ProductID:X4} out={outLen} in={inLen} feat={featLen}");
            Log($"   {shortPath}");

            // Method 1: HidSharp open + Write (Output report)
            string m1 = TryHidSharpWriteAll(dev, seq, outLen);
            Log($"   [1] HidSharp Write : {m1}");

            Thread.Sleep(80);

            // Method 2: CreateFile + HidD_SetOutputReport
            string m2 = TryHidDSetOutputAll(dev, seq, outLen);
            Log($"   [2] HidD_SetOutput : {m2}");

            Thread.Sleep(80);

            // Method 3: HidSharp SetFeature
            string m3 = TrySetFeatureAll(dev, seq, featLen);
            Log($"   [3] SetFeature     : {m3}");

            Log($"   ⏱ 请在 {interfaceDelayMs}ms 内拉 LT 和 RT 感受是否变化");
            Thread.Sleep(interfaceDelayMs);
        }

        Log("✅ 广播完成。请回忆哪一秒 LT 或 RT 有变化，将那次对应的 PID+方法告诉我。");
        return sb.ToString();
    }

    private static int SafeInt(Func<int> f) { try { return f(); } catch { return -1; } }

    private static string TryHidSharpWriteAll(HidDevice dev, byte[][] seq, int outLen)
    {
        if (outLen <= 0) return "跳过(output<=0)";
        try
        {
            using var s = dev.Open();
            foreach (var pkt in seq)
            {
                var buf = new byte[outLen];
                Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, outLen));
                s.Write(buf, 0, outLen);
                Thread.Sleep(12);
            }
            return "OK";
        }
        catch (Exception ex) { return $"FAIL {ex.GetType().Name}: {ex.Message}"; }
    }

    private static string TryHidDSetOutputAll(HidDevice dev, byte[][] seq, int outLen)
    {
        if (outLen <= 0) return "跳过(output<=0)";
        nint h = CreateFile(dev.DevicePath, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
        if (h == new nint(-1)) return $"CreateFile 失败 err={Marshal.GetLastWin32Error()}";
        try
        {
            foreach (var pkt in seq)
            {
                var buf = new byte[outLen];
                Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, outLen));
                if (!HidD_SetOutputReport(h, buf, outLen))
                    return $"FAIL err={Marshal.GetLastWin32Error()}";
                Thread.Sleep(12);
            }
            return "OK";
        }
        finally { CloseHandle(h); }
    }

    private static string TrySetFeatureAll(HidDevice dev, byte[][] seq, int featLen)
    {
        if (featLen <= 0) return "跳过(feat<=0)";
        try
        {
            using var s = dev.Open();
            foreach (var pkt in seq)
            {
                var buf = new byte[featLen];
                Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, featLen));
                s.SetFeature(buf);
                Thread.Sleep(12);
            }
            return "OK";
        }
        catch (Exception ex) { return $"FAIL {ex.GetType().Name}: {ex.Message}"; }
    }

    /// <summary>
    /// Check whether SpaceStationService process is currently running.
    /// </summary>
    public static bool IsSpaceStationRunning()
    {
        try
        {
            return Process.GetProcessesByName("SpaceStationService").Length > 0
                || Process.GetProcessesByName("Flydigi Space Station").Length > 0
                || Process.GetProcessesByName("FlydigiSpaceStation").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Broadcast MAXIMUM strength rumble (Report 0x05 sub 0x0f, all motors 255)
    /// to every FlyDigi HID interface, using 3 send mechanisms per interface.
    /// If the controller vibrates at any point, that interface+method is writable.
    /// This isolates "interface reachable" from "ForceAdapt protocol correct".
    /// </summary>
    /// <param name="perMethodMs">Silent wait after EACH individual sub-method so the user can identify which one triggered the vibration.</param>
    /// <param name="perInterfaceMs">Additional pause between whole interfaces.</param>
    public static string BroadcastStrongRumbleToAllInterfaces(int perMethodMs, int perInterfaceMs, Action<string> perLineLog)
    {
        var sb = new System.Text.StringBuilder();
        var devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => d.VendorID == ForceAdaptProtocol.VendorId)
            .OrderBy(d => d.ProductID)
            .ThenBy(d => d.GetMaxOutputReportLength())
            .ToArray();

        // Max rumble: Report 0x05 sub 0x0f, left/right main + left/right trigger all = 255
        var rumble = ForceAdaptProtocol.BuildRumbleCommand(255, 255, 255, 255);
        // Vendor cmd 0x12 HAPTIC (SDL naming), strong values
        var vendorHaptic = BuildVendorHaptic();
        void Log(string line) { sb.AppendLine(line); perLineLog?.Invoke(line); }

        Log($"💢 枚举到 {devices.Length} 个 FlyDigi HID 接口, 依次广播强振动+震动命令");
        Log($"   每个子方法之间等待 {perMethodMs}ms, 接口之间额外 {perInterfaceMs}ms");
        Log($"   ⚠ 请盯着下一行日志 + 手感, 振动瞬间对应哪一行就是那个方法生效");

        int idx = 0;
        foreach (var dev in devices)
        {
            idx++;
            int outLen = SafeInt(() => dev.GetMaxOutputReportLength());
            int featLen = SafeInt(() => dev.GetMaxFeatureReportLength());
            string shortPath = dev.DevicePath.Length > 48
                ? "…" + dev.DevicePath[^48..]
                : dev.DevicePath;
            Log($"── [{idx}/{devices.Length}] PID=0x{dev.ProductID:X4} out={outLen} feat={featLen}");
            Log($"   {shortPath}");

            SendAndWait("1a HidSharp Write  0x05 Rumble", () => TrySingleHidSharpWrite(dev, rumble, outLen), perMethodMs, Log);
            SendAndWait("1b HidSharp Write  0x12 Haptic", () => TrySingleHidSharpWrite(dev, vendorHaptic, outLen), perMethodMs, Log);
            SendAndWait("2a HidD_SetOutput  0x05 Rumble", () => TrySingleHidDSetOutput(dev, rumble, outLen), perMethodMs, Log);
            SendAndWait("2b HidD_SetOutput  0x12 Haptic", () => TrySingleHidDSetOutput(dev, vendorHaptic, outLen), perMethodMs, Log);
            SendAndWait("3a SetFeature      0x05 Rumble", () => TrySingleSetFeature(dev, rumble, featLen), perMethodMs, Log);
            SendAndWait("3b SetFeature      0x12 Haptic", () => TrySingleSetFeature(dev, vendorHaptic, featLen), perMethodMs, Log);

            Log($"   ── 接口 {idx} 完成, 额外等 {perInterfaceMs}ms 再换下一个 ──");
            Thread.Sleep(perInterfaceMs);
        }

        Log("✅ 广播完成. 振动瞬间对应的那一行 [Xx] 就是答案");
        return sb.ToString();
    }

    /// <summary>Send one packet via a specific method, then idle the requested duration so the user can feel whether THIS method caused vibration.</summary>
    private static void SendAndWait(string label, Func<string> sendFunc, int waitMs, Action<string> log)
    {
        log($"   ▶ 发送 [{label}] ...");
        string result = sendFunc();
        log($"     [{label}] : {result}   —— 接下来 {waitMs}ms 静默, 感受振动 ——");
        Thread.Sleep(waitMs);
    }

    /// <summary>Build a 32B vendor haptic command (SDL FLYDIGI_V2_HAPTIC_COMMAND 0x12) with max power.</summary>
    private static byte[] BuildVendorHaptic()
    {
        var buf = new byte[32];
        buf[0] = ForceAdaptProtocol.VendorProtocol.ReportId; // 0x03
        buf[1] = 0x5A; buf[2] = 0xA5;
        buf[3] = ForceAdaptProtocol.VendorProtocol.CmdHaptic; // 0x12
        buf[4] = 0x04; // payload 4B
        buf[5] = 0xFF; buf[6] = 0xFF; // left/right main rumble
        buf[7] = 0xFF; buf[8] = 0xFF; // left/right trigger rumble
        return buf;
    }

    private static string TrySingleHidSharpWrite(HidDevice dev, byte[] pkt, int outLen)
    {
        if (outLen <= 0) return "跳过(output<=0)";
        try
        {
            using var s = dev.Open();
            var buf = new byte[outLen];
            Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, outLen));
            s.Write(buf, 0, outLen);
            return "OK";
        }
        catch (Exception ex) { return $"FAIL {ex.GetType().Name}: {ex.Message}"; }
    }

    private static string TrySingleHidDSetOutput(HidDevice dev, byte[] pkt, int outLen)
    {
        if (outLen <= 0) return "跳过(output<=0)";
        nint h = CreateFile(dev.DevicePath, 0xC0000000, 3, nint.Zero, 3, 0, nint.Zero);
        if (h == new nint(-1)) return $"CreateFile err={Marshal.GetLastWin32Error()}";
        try
        {
            var buf = new byte[outLen];
            Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, outLen));
            return HidD_SetOutputReport(h, buf, outLen) ? "OK" : $"FAIL err={Marshal.GetLastWin32Error()}";
        }
        finally { CloseHandle(h); }
    }

    private static string TrySingleSetFeature(HidDevice dev, byte[] pkt, int featLen)
    {
        if (featLen <= 0) return "跳过(feat<=0)";
        try
        {
            using var s = dev.Open();
            var buf = new byte[featLen];
            Array.Copy(pkt, 0, buf, 0, Math.Min(pkt.Length, featLen));
            s.SetFeature(buf);
            return "OK";
        }
        catch (Exception ex) { return $"FAIL {ex.GetType().Name}: {ex.Message}"; }
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

