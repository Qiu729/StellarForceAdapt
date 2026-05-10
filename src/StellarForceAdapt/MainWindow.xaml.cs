using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StellarForceAdapt.HID;
using StellarForceAdapt.Mapping;
using StellarForceAdapt.Monitor;

namespace StellarForceAdapt;

public partial class MainWindow : Window
{
    // Win32 console attach so our WPF app gets a real resizable/scrollable log window.
    // Far more readable than the cramped in-app ListBox when debugging HID traffic.
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleOutputCP(uint wCodePageID);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleTitle(string lpConsoleTitle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(nint hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleScreenBufferSize(nint hConsoleOutput, COORD dwSize);
    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SMALL_RECT { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct CONSOLE_SCREEN_BUFFER_INFO
    { public COORD dwSize, dwCursorPosition; public short wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }

    private static bool _consoleAttached;
    private readonly FlyDigiDevice _device = new();
    private readonly HIDGamepadReader _gamepad = new();
    private readonly StellarBladeMonitor _gameMonitor = new();
    private readonly MappingEngine _engine;
    private readonly CancellationTokenSource _uiCts = new();
    private ControllerMapping _mapping = new();
    private byte[]? _idleState;

    private bool _isRunning;
    private int _logCount;
    private readonly List<(string Path, TriggerProfile Profile)> _profiles = [];
    private readonly string _profilesDir;
    private readonly string _mappingPath;
    private readonly string _logFilePath;

    // Binding state
    private string? _bindingTarget;
    private byte[]? _bindingIdle;

    // Reconnection guard
    private bool _isReconnecting;

    public string VersionText { get; } = "v1.0.0 · 飞智八爪鱼5";

    public MainWindow()
    {
        EnsureConsoleAttached();
        InitializeComponent();
        DataContext = this;

        // Try multiple paths for profiles (BaseDirectory vs exe location)
        var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var baseDir = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles"))
            ? AppDomain.CurrentDomain.BaseDirectory
            : exeDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _profilesDir = Path.Combine(baseDir, "Profiles");
        _mappingPath = Path.Combine(_profilesDir, "controller_mapping.json");
        _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
        File.WriteAllText(_logFilePath, $"=== StellarForceAdapt Log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n");
        if (_consoleAttached)
        {
            Console.WriteLine($"[init] 日志文件: {_logFilePath}");
            Console.WriteLine($"[init] PowerShell 实时跟踪: Get-Content -Wait -Tail 40 '{_logFilePath}'");
        }

        _engine = new MappingEngine(_device, _gamepad, _gameMonitor)
        {
            ButtonMapping = _mapping
        };

        // Wire events
        _device.ConnectionChanged += OnControllerConnectionChanged;
        _device.InputReportReceived += OnControllerInputReport;
        _gameMonitor.GameProcessChanged += OnGameProcessChanged;
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.GameStateUpdate += OnGameStateUpdate;
        _engine.EffectTriggered += OnEffectTriggered;

        // Load mapping
        _mapping = ControllerMapping.Load(_mappingPath);
        Log($"📋 已加载 {_mapping.Mappings.Count} 个按键映射");

        // Initial scans
        RefreshProfiles_Click(null!, null!);
        ScanController();
        StartUiTimer();

        Debug.WriteLine("[UI] Initialized");

        // Auto-run diagnostics after startup (background thread, non-blocking)
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000);
            AutoDiagnostics();
        });
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiCts.Cancel();
        _engine.Stop();
        _device.Dispose();
        _gamepad.Dispose();
        _gameMonitor.Dispose();
        base.OnClosed(e);
    }

    // ---- UI Updates ----

    private void StartUiTimer()
    {
        var timer = new System.Timers.Timer(50); // 20Hz UI updates
        timer.Elapsed += (_, _) =>
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateTriggerDisplay();
                    UpdateStatusBar();
                    UpdateDebugDisplay();
                    CheckBinding();
                });
            }
            catch { }
        };
        timer.Start();
    }

    private void UpdateTriggerDisplay()
    {
        var state = _gamepad.CurrentState;
        if (!state.Connected) return;

        LeftTriggerBar.Value = state.LeftTrigger;
        RightTriggerBar.Value = state.RightTrigger;
        LeftTriggerValue.Text = state.LeftTrigger.ToString();
        RightTriggerValue.Text = state.RightTrigger.ToString();
    }

    private void UpdateStatusBar()
    {
        var state = _gamepad.CurrentState;
        var connected = state.Connected;
        var gameRunning = _gameMonitor.IsGameRunning;

        // Controller status
        ControllerStatus.Background = connected
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : (SolidColorBrush)FindResource("BgMedium");
        ControllerText.Text = connected
            ? $"🎮 手柄: 已连接 ({_device.DeviceName ?? "八爪鱼5"})"
            : "🎮 手柄: 未连接";

        // Game status
        GameStatus.Background = gameRunning
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : (SolidColorBrush)FindResource("BgMedium");
        GameText.Text = gameRunning ? "🎯 游戏: 剑星运行中" : "🎯 游戏: 未检测";

        // Engine status
        EngineStatus.Background = _isRunning
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(244, 67, 54));
        EngineText.Text = _isRunning ? "⚙️ 引擎: 运行中" : "⚙️ 引擎: 停止";
    }

    // ---- Event Handlers ----

    private void OnControllerConnectionChanged(object? sender, bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                Log("✅ 手柄已连接");
                SetStatus("控制器已就绪");
            }
            else
            {
                Log("❌ 手柄断开连接");
                SetStatus("手柄断开，正在重试...");
                if (_isReconnecting) return; // prevent recursion from TryReconnect → Disconnect
                _isReconnecting = true;
                _ = Task.Run(async () =>
                {
                    while (!_device.IsConnected && _isReconnecting)
                    {
                        await Task.Delay(2000);
                        if (_device.TryReconnect())
                        {
                            Dispatcher.Invoke(() => Log("✅ 手柄已重连"));
                            break;
                        }
                    }
                    _isReconnecting = false;
                });
            }
        });
    }

    private void OnControllerInputReport(object? sender, byte[] data)
    {
        Log($"📥 CD2 输入 ({data.Length}B): {BitConverter.ToString(data)}");
    }

    private void OnGameProcessChanged(object? sender, bool running)
    {
        Dispatcher.Invoke(() =>
        {
            if (running)
            {
                Log("🎯 检测到剑星游戏进程");
                SetStatus("游戏已检测，引擎就绪");
                if (!_isRunning)
                {
                    ToggleButton.Content = "▶ 启动引擎";
                }
            }
            else
            {
                if (_isRunning) return;
                Log("⏹ 游戏已退出");
                SetStatus("等待游戏启动...");
            }
        });
    }

    private void OnEngineStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() => SetStatus(status));
    }

    private void OnGameStateUpdate(object? sender, GameState state)
    {
        Dispatcher.Invoke(() =>
        {
            ActionText.Text = state.PlayerAction.ToString();
            CombatText.Text = state.InCombat ? "⚔️ 战斗中" : "✅ 安全";

            var combo = state.ComboCount > 0 ? state.ComboCount.ToString() : "-";
            ComboText.Text = combo;
            SourceText.Text = state.DetectionSource switch
            {
                DetectionSource.Memory => "内存读取",
                DetectionSource.XInput => "XInput 推断",
                _ => "-",
            };

            LeftEffectText.Text = state.PlayerAction switch
            {
                PlayerAction.Blocking => "🔒 格挡阻力",
                PlayerAction.Sprinting => "💨 奔跑震动",
                _ => "-",
            };
            RightEffectText.Text = state.PlayerAction switch
            {
                PlayerAction.ShootingWeapon => "🔫 射击后坐力",
                PlayerAction.Aiming => "🎯 瞄准阻力",
                PlayerAction.MeleeAttack => "⚔️ 攻击反馈",
                PlayerAction.Sprinting => "💨 奔跑震动",
                _ => "-",
            };
        });
    }

    private void OnEffectTriggered(object? sender, string effectName)
    {
        Dispatcher.Invoke(() => Log($"⚡ 触发: {effectName}"));
    }

    // ---- Button Handlers ----

    private void ToggleEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            StopEngine();
        }
        else
        {
            StartEngine();
        }
    }

    private void StartEngine()
    {
        if (!_device.IsConnected)
        {
            if (!_device.Connect())
            {
                MessageBox.Show("无法连接到手柄，请确保八爪鱼5已通过 USB 连接", "连接失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Load selected profile
        if (ProfileCombo.SelectedItem is TriggerProfile profile)
        {
            _engine.SetProfile(profile);
        }
        else if (_profiles.Count > 0)
        {
            _engine.SetProfile(_profiles[0].Profile);
            ProfileCombo.SelectedIndex = 0;
        }

        _engine.Start();
        _isRunning = true;
        ToggleButton.Content = "⏹ 停止引擎";
        ToggleButton.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        Log("▶ 引擎已启动");
    }

    private void StopEngine()
    {
        _engine.Stop();
        _isRunning = false;
        ToggleButton.Content = "▶ 启动引擎";
        ToggleButton.Background = (Brush)FindResource("Accent");
        Log("⏹ 引擎已停止");
        SetStatus("引擎已停止");
    }

    private void ScanController()
    {
        var devices = FlyDigiDevice.ScanDevices();
        if (devices.Length > 0)
        {
            var known = devices.FirstOrDefault(d => d.IsKnown);
            if (known != null)
            {
                Log($"📡 检测到飞智手柄: {known.ProductName} (PID=0x{known.ProductId:X4})");
            }
            else
            {
                Log($"📡 检测到未知飞智设备: {devices[0].ProductName}");
            }

            bool cd2Ok = _device.Connect();
            bool hidOk = _gamepad.Connect();

            if (cd2Ok && hidOk)
                Log("✅ 手柄连接成功 (CD2+输入)");
            else if (cd2Ok)
                Log("⚠️ 手柄输出已连接，输入未连接");
            else
                Log("⚠️ 手柄连接失败，请确认已通过 USB 连接");

            // Capture idle state for debug display
            if (hidOk)
            {
                _idleState = _gamepad.CaptureIdle();
                if (_idleState != null)
                    Log($"📊 空闲状态已捕获: {BitConverter.ToString(_idleState)}");
                else
                    Log("⚠️ 无法捕获空闲状态");
            }
        }
        else
        {
            Log("🔍 未检测到飞智手柄，请连接八爪鱼5");
        }
    }

    private void RefreshProfiles_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Clear();
        ProfileCombo.Items.Clear();

        var loaded = TriggerProfile.LoadAll(_profilesDir);
        foreach (var (path, profile) in loaded)
        {
            _profiles.Add((path, profile));
            ProfileCombo.Items.Add(profile);
        }

        if (loaded.Count > 0)
        {
            ProfileCombo.SelectedIndex = 0;
            Log($"📂 已加载 {loaded.Count} 个配置文件");
        }
        else
        {
            Log("⚠️ 未找到配置文件");
        }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is TriggerProfile profile)
        {
            ProfileDesc.Text = $"{profile.Description}\n版本: {profile.Version} · 规则数: {profile.Rules.Count}";
            if (_isRunning)
                _engine.SetProfile(profile);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogList.Items.Clear();
        _logCount = 0;
    }

    // ---- Controller Debug Display ----

    private void UpdateDebugDisplay()
    {
        var state = _gamepad.CurrentState;
        if (!state.Connected || state.Raw == null || state.Raw.Length == 0) return;

        var raw = state.Raw;

        // Raw HID hex
        RawHidText.Text = BitConverter.ToString(raw);

        // Byte changes from idle
        if (_idleState != null)
        {
            var changes = new List<string>();
            for (int i = 0; i < raw.Length && i < _idleState.Length; i++)
            {
                if (raw[i] != _idleState[i])
                    changes.Add($"B{i}:{_idleState[i]:X2}→{raw[i]:X2}");
            }
            RawChangesText.Text = changes.Count > 0
                ? "变化: " + string.Join(", ", changes)
                : "无变化";
        }
        else
        {
            _idleState = (byte[])raw.Clone();
        }

        // Button state indicators
        SetButtonLight("A", raw);
        SetButtonLight("B", raw);
        SetButtonLight("X", raw);
        SetButtonLight("Y", raw);
        SetButtonLight("LB", raw);
        SetButtonLight("RB", raw);
        SetButtonLight("LT", raw);
        SetButtonLight("RT", raw);

    }

    private void SetButtonLight(string name, byte[] raw)
    {
        var border = FindName($"Btn_{name}") as System.Windows.Controls.Border;
        if (border == null) return;

        bool pressed;
        if (name is "LT" or "RT")
        {
            var val = name == "LT"
                ? _gamepad.CurrentState.LeftTrigger
                : _gamepad.CurrentState.RightTrigger;
            pressed = val > 30;
        }
        else
        {
            pressed = _mapping.IsPressed(name, raw);
        }

        border.Background = pressed
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(51, 51, 51));
    }

    // ---- Button Binding ----

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn)
        {
            _bindingTarget = btn.Content?.ToString() switch
            {
                "A" => "A", "B" => "B", "X" => "X", "Y" => "Y",
                "LB" => "LB", "RB" => "RB",
                "LT" => "LT", "RT" => "RT",
                _ => _bindingTarget
            };

            // Capture idle state
            _bindingIdle = _gamepad.CaptureIdle();
            BindStatusText.Text = $"⏳ 请按下 [{_bindingTarget}] 键...";
            BindStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // orange
        }
    }

    private void CheckBinding()
    {
        if (_bindingTarget == null || _bindingIdle == null) return;

        var state = _gamepad.CurrentState;
        if (state.Raw == null || state.Raw.Length == 0) return;

        // Find which byte/bit changed from idle
        for (int i = 0; i < state.Raw.Length && i < _bindingIdle.Length; i++)
        {
            byte diff = (byte)(state.Raw[i] ^ _bindingIdle[i]);
            if (diff != 0)
            {
                // Find which bit changed
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((diff & (1 << bit)) != 0)
                    {
                        _mapping.SetMapping(_bindingTarget, i, bit);
                        BindStatusText.Text = $"✅ {_bindingTarget} → B{i} bit {bit}";
                        BindStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // green
                        Log($"🔗 绑定: {_bindingTarget} → B{i}:{1 << bit:X2} (bit {bit})");
                        _bindingTarget = null;
                        _bindingIdle = null;
                        _idleState = (byte[])state.Raw.Clone(); // update idle
                        return;
                    }
                }
            }
        }
    }

    private void SaveMapping_Click(object sender, RoutedEventArgs e)
    {
        _mapping.Save(_mappingPath);
        Log($"💾 已保存 {_mapping.Mappings.Count} 个按键映射到 {_mappingPath}");
        SetStatus("映射已保存");
    }

    private void ResetMapping_Click(object sender, RoutedEventArgs e)
    {
        _mapping = new ControllerMapping();
        _mapping.Save(_mappingPath);
        Log("🔄 映射已重置，请重新绑定按键");
        SetStatus("映射已重置");
    }

    // ---- Manual Test ----

    private void TestForceAdaptRT_Click(object sender, RoutedEventArgs e)
    {
        Log($"🔍 设备状态: CD2连接={_device.IsConnected}, PID=0x{_device.ProductId:X4}, {_device.DeviceName ?? "N/A"}");
        if (!_device.IsConnected) { Log("⚠️ 手柄未连接"); return; }

        var cmd = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Resistance,
            triggerPosition: 30, intensity: 220, speed: 180, flags: 0x02);

        bool ok1 = _device.SendReport(cmd);
        Log(ok1 ? $"🔧 [CD2 Write] RT 阻力 → OK: {BitConverter.ToString(cmd, 0, 8)}" : "❌ [CD2 Write] 失败");

        bool ok2 = _device.SendFeatureReport(cmd);
        Log(ok2 ? $"🔧 [CD2 SetFeature] RT 阻力 → OK" : "❌ [CD2 SetFeature] 失败");

        bool ok3 = FlyDigiDevice.SendReportToInterface(0x2501, 13, cmd);
        Log(ok3 ? $"🔧 [mi_02 13B] RT 阻力 → OK" : "❌ [mi_02 13B] 失败（可能被锁定）");

        bool ok4 = FlyDigiDevice.SendReportToInterface(0x2501, 32, cmd);
        Log(ok4 ? $"🔧 [mi_02 32B] RT 阻力 → OK" : "❌ [mi_02 32B] 失败（可能被锁定）");

        var cmd13 = new byte[13];
        Array.Copy(cmd, cmd13, Math.Min(cmd.Length, 13));
        bool ok5 = _device.SendReport(cmd13);
        Log(ok5 ? $"🔧 [CD2 13B] RT 阻力 → OK: {BitConverter.ToString(cmd13)}" : "❌ [CD2 13B] 失败");

        var cmd32 = new byte[32];
        Array.Copy(cmd, cmd32, Math.Min(cmd.Length, 32));
        bool ok6 = _device.SendReport(cmd32);
        Log(ok6 ? $"🔧 [CD2 32B] RT 阻力 → OK" : "❌ [CD2 32B] 失败");

        AutoClear(3000);
    }

    private void TestForceAdaptVibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected) { Log("⚠️ 手柄未连接"); return; }

        var cmd = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Vibration,
            triggerPosition: 150, intensity: 200, speed: 200, flags: 0x02);

        bool ok1 = _device.SendReport(cmd);
        Log(ok1 ? $"🔧 [Write] RT 振动 → OK" : "❌ [Write] RT 振动 失败");

        bool ok2 = _device.SendFeatureReport(cmd);
        Log(ok2 ? $"🔧 [SetFeature] RT 振动 → OK" : "❌ [SetFeature] RT 振动 失败");

        AutoClear(2000);
    }

    private void TestForceAdaptLT_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected) { Log("⚠️ 手柄未连接"); return; }

        var cmd = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Resistance,
            triggerPosition: 60, intensity: 200, speed: 180, flags: 0x01);

        bool ok1 = _device.SendReport(cmd);
        Log(ok1 ? $"🔧 [Write] LT 阻力 → OK" : "❌ [Write] LT 阻力 失败");

        bool ok2 = _device.SendFeatureReport(cmd);
        Log(ok2 ? $"🔧 [SetFeature] LT 阻力 → OK" : "❌ [SetFeature] LT 阻力 失败");

        AutoClear(2000);
    }

    private void TestRumble_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected) { Log("⚠️ 手柄未连接"); return; }

        var cmd = ForceAdaptProtocol.BuildRumbleCommand(leftTriggerRumble: 200, rightTriggerRumble: 200);

        bool ok1 = _device.SendReport(cmd);
        Log(ok1 ? $"🔧 [Write] 扳机马达 → OK" : "❌ [Write] 扳机马达 失败");

        bool ok2 = _device.SendFeatureReport(cmd);
        Log(ok2 ? $"🔧 [SetFeature] 扳机马达 → OK" : "❌ [SetFeature] 扳机马达 失败");

        AutoClear(1000);
    }

    private void EnumInterfaces_Click(object sender, RoutedEventArgs e)
    {
        var diag = FlyDigiDevice.GetInterfaceDiagnostics();
        Log($"🔍 HID 接口枚举:");
        foreach (var line in diag.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log($"  {line.Trim()}");
    }

    private void AutoDiagnostics()
    {
        Log("🔬 自动诊断启动...");

        // Prevent reconnection loop from interfering with diagnostics
        _isReconnecting = true;

        // Don't kill SpaceStationService — doing so breaks HID driver state
        // Instead, access mi_02 col01 (32B) with correct Report 0x03 protocol
        var serviceProcs = Process.GetProcessesByName("SpaceStationService");
        if (serviceProcs.Length > 0)
        {
            Log($"ℹ️ SpaceStationService 正在运行 (PID {serviceProcs[0].Id}) — 保持运行");
        }

        // Re-enumerate interfaces
        Log("🔍 枚举 HID 接口:");
        var diag = FlyDigiDevice.GetInterfaceDiagnostics();
        foreach (var line in diag.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Log($"  {line.Trim()}");

        // --- New: Send 5aa5 protocol directly to mi_02 col01 (32B) with Report 0x03 ---
        Log("🔗 测试 5aa5 厂商协议 → mi_02 32B (Report 0x03)...");
        var vendorCmd = ForceAdaptProtocol.VendorProtocol.BuildBeginConfig();
        string r0 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, vendorCmd);
        Log(r0 == "OK" ? "🔧 [mi_02 5aa5 开始配置] OK!" : $"❌ [mi_02 5aa5 开始配置] {r0}");

        Thread.Sleep(30);
        vendorCmd = ForceAdaptProtocol.VendorProtocol.BuildSetEffect(mode: 2, intensity: 200);
        r0 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, vendorCmd);
        Log(r0 == "OK" ? "🔧 [mi_02 5aa5 设置阻力] OK!" : $"❌ [mi_02 5aa5 设置阻力] {r0}");

        Thread.Sleep(30);
        vendorCmd = ForceAdaptProtocol.VendorProtocol.BuildEndConfig();
        r0 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, vendorCmd);
        Log(r0 == "OK" ? "🔧 [mi_02 5aa5 结束配置] OK!" : $"❌ [mi_02 5aa5 结束配置] {r0}");

        // Also try 13B interface with 5aa5 protocol (padded)
        Log("🔗 测试 5aa5 厂商协议 → mi_02 13B...");
        var vendorCmd13 = new byte[13];
        Array.Copy(vendorCmd, vendorCmd13, Math.Min(vendorCmd.Length, 13));
        string r0_13 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 13, vendorCmd13);
        Log(r0_13 == "OK" ? "🔧 [mi_02 13B 5aa5] OK!" : $"❌ [mi_02 13B 5aa5] {r0_13}");

        // Legacy tests for comparison
        Log("🔗 尝试 mi_02 多种格式（旧协议 0x06）...");
        var cmdFA = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Resistance,
            triggerPosition: 30, intensity: 220, speed: 180, flags: 0x02);

        // mi_02 32B: original format [0x06, mode, pos, int, speed, flags...]
        var cmd32orig = new byte[32];
        Array.Copy(cmdFA, cmd32orig, Math.Min(cmdFA.Length, 32));
        string r1 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, cmd32orig);
        Log(r1 == "OK" ? "🔧 [mi_02 32B 原始] OK" : $"❌ [mi_02 32B 原始] {r1}");

        // mi_02 32B: no report ID [0x00, mode, pos, int, speed, flags...]
        var cmd32a = new byte[32];
        cmd32a[0] = 0x00;
        cmd32a[1] = 0x01;
        cmd32a[2] = 30;
        cmd32a[3] = 220;
        cmd32a[4] = 180;
        cmd32a[5] = 0x02;
        string r2 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, cmd32a);
        Log(r2 == "OK" ? "🔧 [mi_02 32B 00前缀] OK" : $"❌ [mi_02 32B 00前缀] {r2}");

        // mi_02 32B: rumble [0x05, 0x0f, 0, 0, left, right...]
        var rumble32 = new byte[32];
        rumble32[0] = 0x05;
        rumble32[1] = 0x0f;
        rumble32[4] = 200;
        rumble32[5] = 200;
        string r3 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 32, rumble32);
        Log(r3 == "OK" ? "🔧 [mi_02 32B rumble] OK" : $"❌ [mi_02 32B rumble] {r3}");

        // mi_02 13B: try this interface too [0x06, mode, pos, int, speed, flags...]
        var cmd13 = new byte[13];
        cmd13[0] = 0x06;
        cmd13[1] = 0x01;
        cmd13[2] = 30;
        cmd13[3] = 220;
        cmd13[4] = 180;
        cmd13[5] = 0x02;
        string r4 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 13, cmd13);
        Log(r4 == "OK" ? "🔧 [mi_02 13B 06前缀] OK" : $"❌ [mi_02 13B 06前缀] {r4}");

        // mi_02 13B: without 0x06 [mode, pos, int, speed, flags...]
        var cmd13b = new byte[13];
        cmd13b[0] = 0x01;
        cmd13b[1] = 30;
        cmd13b[2] = 220;
        cmd13b[3] = 180;
        cmd13b[4] = 0x02;
        string r5 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 13, cmd13b);
        Log(r5 == "OK" ? "🔧 [mi_02 13B 无前缀] OK" : $"❌ [mi_02 13B 无前缀] {r5}");

        // mi_02 13B: 0x00 prefix [0x00, mode, pos, int, speed, flags...]
        var cmd13c = new byte[13];
        cmd13c[0] = 0x00;
        cmd13c[1] = 0x01;
        cmd13c[2] = 30;
        cmd13c[3] = 220;
        cmd13c[4] = 180;
        cmd13c[5] = 0x02;
        string r6 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 13, cmd13c);
        Log(r6 == "OK" ? "🔧 [mi_02 13B 00前缀] OK" : $"❌ [mi_02 13B 00前缀] {r6}");

        // mi_02 13B: rumble
        var rumble13 = new byte[13];
        rumble13[0] = 0x05;
        rumble13[1] = 0x0f;
        rumble13[4] = 200;
        rumble13[5] = 200;
        string r7 = FlyDigiDevice.SendReportToInterfaceDebug(0x2501, 13, rumble13);
        Log(r7 == "OK" ? "🔧 [mi_02 13B rumble] OK" : $"❌ [mi_02 13B rumble] {r7}");

        // Try ConnectMi02 for persistent connection testing
        Log("🔗 尝试 ConnectMi02...");
        var (miConnected, miError) = _device.ConnectMi02Debug();
        if (miConnected)
        {
            Log("✅ mi_02 持久连接成功！");
            bool okFA = _device.SendForceAdaptMi02(
                mode: (byte)ForceAdaptProtocol.ForceAdaptMode.Resistance,
                position: 30, intensity: 220, speed: 180, flags: 0x02);
            Log(okFA ? "🔧 [mi_02 persist] RT 阻力 → OK！" : "❌ [mi_02 persist] 失败");

            if (_device.IsConnected)
            {
                bool okRumble = _device.SendForceAdaptMi02(
                    mode: (byte)ForceAdaptProtocol.ForceAdaptMode.Vibration,
                    position: 150, intensity: 200, speed: 200, flags: 0x02);
                Log(okRumble ? "🔧 [mi_02 persist] RT 振动 → OK！" : "❌ [mi_02 persist] 振动失败");
            }
        }
        else
        {
            Log($"❌ mi_02 连接失败: {miError}");
        }

        // Restore CD2 connection (ConnectMi02 replaces _device/_stream)
        Log("🔄 恢复 CD2 连接...");
        _device.Connect();

        // Test CD2 with multiple formats
        Log("--- CD2 多种格式测试 ---");
        bool cd2ok;

        // Format 1: Standard ForceAdapt (report 0x06 at byte 0)
        cd2ok = _device.SendReport(cmdFA);
        Log(cd2ok ? "🔧 [CD2 标准] 已发送 (06-01-1E-DC-B4-02)" : "❌ [CD2 标准] 失败");

        // Format 2: ForceAdapt with 0x00 prefix (report ID at byte 0, 0x06 at byte 1)
        var cmd2 = new byte[65];
        cmd2[1] = 0x06; cmd2[2] = 0x01; cmd2[3] = 30; cmd2[4] = 220; cmd2[5] = 180; cmd2[6] = 0x02;
        cd2ok = _device.SendReport(cmd2);
        Log(cd2ok ? "🔧 [CD2 00前缀] 已发送" : "❌ [CD2 00前缀] 失败");

        // Format 3: Rumble command on CD2
        var rumble = ForceAdaptProtocol.BuildRumbleCommand(leftTriggerRumble: 200, rightTriggerRumble: 200);
        cd2ok = _device.SendReport(rumble);
        Log(cd2ok ? "🔧 [CD2 扳机马达] 已发送 (200,200)" : "❌ [CD2 扳机马达] 失败");

        // Format 4: DualSense-style trigger effect (offset 2, mode at byte 3)
        var cmd4 = new byte[65];
        cmd4[2] = 0x06; cmd4[3] = 0x01; cmd4[4] = 30; cmd4[5] = 220; cmd4[6] = 180; cmd4[7] = 0x02;
        cd2ok = _device.SendReport(cmd4);
        Log(cd2ok ? "🔧 [CD2 offset2] 已发送" : "❌ [CD2 offset2] 失败");

        // Format 5: Reset triggers via CD2
        _device.ResetTriggers();
        Log("🔧 [CD2 复位] 已发送");

        Thread.Sleep(200);

        // Format 6: Continuous resistance mode (may need to hold the effect)
        var cmd6 = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Resistance,
            triggerPosition: 10, intensity: 255, speed: 255, flags: 0x02);
        cd2ok = _device.SendReport(cmd6);
        Log(cd2ok ? "🔧 [CD2 最大阻力] 已发送 (pos=10,int=255,spd=255)" : "❌ [CD2 最大阻力] 失败");

        // Format 7: ForceAdapt with both triggers
        var cmd7 = ForceAdaptProtocol.BuildForceAdaptCommand(
            ForceAdaptProtocol.ForceAdaptMode.Resistance,
            triggerPosition: 50, intensity: 200, speed: 180, flags: 0x03);
        cd2ok = _device.SendReport(cmd7);
        Log(cd2ok ? "🔧 [CD2 双扳机] 已发送" : "❌ [CD2 双扳机] 失败");

        // Format 8: DualSense-style (report ID 0x31, trigger data at offset 43)
        var cmd8 = new byte[65];
        cmd8[0] = 0x31;
        cmd8[43] = 0x01; cmd8[47] = 0x01; // cont. resistance both triggers
        cd2ok = _device.SendReport(cmd8);
        Log(cd2ok ? "🔧 [CD2 DualSense1] 已发送" : "❌ [CD2 DualSense1] 失败");

        // Format 9: DualSense section resistance
        var cmd9 = new byte[65];
        cmd9[0] = 0x31;
        cmd9[43] = 0x02; cmd9[44] = 30; cmd9[45] = 220;  // right section
        cmd9[47] = 0x02; cmd9[48] = 50; cmd9[49] = 200;  // left section
        cd2ok = _device.SendReport(cmd9);
        Log(cd2ok ? "🔧 [CD2 DualSense2] 已发送" : "❌ [CD2 DualSense2] 失败");

        // --- New: 5aa5 Vendor Protocol Test ---
        Log("--- 5aa5 厂商协议测试 (SpaceStation 逆向协议) ---");
        bool vendOk;

        // Test 1: Send begin config
        vendOk = _device.SendVendorCommand(ForceAdaptProtocol.VendorProtocol.BuildBeginConfig());
        Log(vendOk ? "🔧 [5aa5 开始配置] OK" : "❌ [5aa5 开始配置] 失败");
        Thread.Sleep(30);

        // Test 2: Set ForceAdapt effect (mode 2 = weapon resistance)
        vendOk = _device.SendVendorCommand(
            ForceAdaptProtocol.VendorProtocol.BuildSetEffect(mode: 2, intensity: 200));
        Log(vendOk ? "🔧 [5aa5 扳机阻力] 已发送 (mode=2, int=200)" : "❌ [5aa5 扳机阻力] 失败");
        Thread.Sleep(50);

        // Test 3: End config
        vendOk = _device.SendVendorCommand(ForceAdaptProtocol.VendorProtocol.BuildEndConfig());
        Log(vendOk ? "🔧 [5aa5 结束配置] OK" : "❌ [5aa5 结束配置] 失败");
        Thread.Sleep(30);

        // Test 4: Complete sequence (begin + set + end)
        Log("--- 5aa5 完整序列测试 ---");
        vendOk = _device.SendVendorForceAdapt(mode: 2, intensity: 200);
        Log(vendOk ? "🔧 [5aa5 完整序列] OK (mode=2)" : "❌ [5aa5 完整序列] 失败");

        // Test 5: Try mode 1 (vibration)
        Thread.Sleep(500);
        Log("--- 5aa5 模式1 (振动) ---");
        vendOk = _device.SendVendorForceAdapt(mode: 1, intensity: 150);
        Log(vendOk ? "🔧 [5aa5 振动] OK (mode=1)" : "❌ [5aa5 振动] 失败");

        // Test 6: Try mode 3
        Thread.Sleep(500);
        Log("--- 5aa5 模式3 ---");
        vendOk = _device.SendVendorForceAdapt(mode: 3, intensity: 150);
        Log(vendOk ? "🔧 [5aa5 模式3] OK (mode=3)" : "❌ [5aa5 模式3] 失败");

        // --- Single-trigger mapping probes (data[8]/data[9] hypothesis) ---
        // Current firmware appears to react to Report 0x03 cmd=0xA5 sub=0x17 only on LT.
        // These probes keep the byte-level LT/RT mapping search alive until the correct
        // cmd set is derived from USBPcap.
        Thread.Sleep(500);
        Log("--- 5aa5 单扳机字节探测 ---");

        var setLtData = ForceAdaptProtocol.VendorProtocol.BuildSetEffectData(2, 200, null);
        setLtData[9] = 0x00; // disable suspected RT byte
        var cmdLtOnly = ForceAdaptProtocol.VendorProtocol.BuildDirectSetEffect(setLtData);
        vendOk = _device.SendVendorCommand(cmdLtOnly);
        Log(vendOk ? "🔧 [LT Only] OK (data[9]=0x00)" : "❌ [LT Only] 失败");

        Thread.Sleep(500);

        var setRtData = ForceAdaptProtocol.VendorProtocol.BuildSetEffectData(2, 200, null);
        setRtData[8] = 0x00; // disable suspected LT byte
        var cmdRtOnly = ForceAdaptProtocol.VendorProtocol.BuildDirectSetEffect(setRtData);
        vendOk = _device.SendVendorCommand(cmdRtOnly);
        Log(vendOk ? "🔧 [RT Only] OK (data[8]=0x00)" : "❌ [RT Only] 失败");

        Log("✅ 诊断完成，请检查扳机是否有物理反馈");
        AutoClear(5000);
        _isReconnecting = false;
    }

    private void StopServiceAndTest_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() => AutoDiagnostics());
    }

    private void TestStopAll_Click(object sender, RoutedEventArgs e)
    {
        _device.ResetTriggers();
        _device.SetForceAdaptEffect(ForceAdaptProtocol.ForceAdaptMode.Off, flags: 0x03);
        Log("⏹ 手动停止: 所有效果已清除");
    }

    /// <summary>
    /// Kill SpaceStation service + process so it releases the mi_02 HID interface,
    /// then reconnect our HID handle so we actually own the write channel.
    /// Without this, SpaceStation's concurrent writes can silently overwrite ours.
    /// </summary>
    private void StopSpaceStationOnly_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🛑 正在关闭 SpaceStation 服务和进程..."));
            bool ok = FlyDigiDevice.StopSpaceStationService();
            Dispatcher.Invoke(() => Log(ok
                ? "✅ SpaceStation 已关闭，准备重连 HID"
                : "⚠ 未发现 SpaceStation 服务/进程（可能已经关闭）"));

            // Give Windows time to release the HID handle
            Thread.Sleep(400);

            bool stillRunning = FlyDigiDevice.IsSpaceStationRunning();
            Dispatcher.Invoke(() => Log(stillRunning
                ? "⚠ SpaceStation 进程仍在运行（可能需要管理员权限终止）"
                : "🔍 SpaceStation 进程已确认关闭"));

            // Re-open our side so we get a fresh, exclusive handle
            bool reconn = _device.TryReconnect();
            Dispatcher.Invoke(() => Log(reconn
                ? "🔌 HID 已重连，现在独占 mi_02，可以重测 Replay/探测按钮"
                : "❌ HID 重连失败，请检查设备是否仍插着"));
        });
    }

    /// <summary>
    /// V2 test button: send a single (side, mode) effect via the new byte-exact
    /// template API. The Tag on the clicked button encodes the pair as
    /// "SIDE:MODE" (e.g. "LT:Vibration").
    /// </summary>
    private void V2ApplyEffect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        var tag = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(tag) || !tag.Contains(':')) return;

        var parts = tag.Split(':', 2);
        if (!Enum.TryParse<StellarForceAdapt.HID.ForceAdaptProtocol.TriggerSide>(parts[0], out var side))
            return;
        if (!Enum.TryParse<StellarForceAdapt.HID.ForceAdaptProtocol.ForceAdaptMode>(parts[1], out var mode))
            return;

        if (!_device.IsConnected)
        {
            Log("⚠️ 手柄未连接");
            return;
        }

        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log($"🚀 V2 发送 [{side} {mode}]..."));
            var (ok, details) = _device.ApplyTriggerEffect(side, mode);
            Dispatcher.Invoke(() => Log($"🚀 V2 {(ok ? "OK" : "部分失败")}  {details}"));
            Dispatcher.Invoke(() => Log($"   ⏱ 现在拉 {side}, 感受{mode}效果"));
        });
    }

    /// <summary>
    /// V2 convenience: clear both triggers back to mode Off (no effect).
    /// </summary>
    private void V2ClearBoth_Click(object sender, RoutedEventArgs e)
    {
        if (!_device.IsConnected) { Log("⚠️ 手柄未连接"); return; }
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🧹 V2 清空双扳机 (LT Off + RT Off)..."));
            var (ok, details) = _device.ApplyTriggerEffectBoth(
                StellarForceAdapt.HID.ForceAdaptProtocol.ForceAdaptMode.Off);
            Dispatcher.Invoke(() => Log($"🧹 {(ok ? "OK" : "失败")}  {details}"));
        });
    }

    /// <summary>
    /// Fire the FULL 6-packet activation sequence for Slot 2:
    /// 0x11 → A4 → A5 → A6 → 0x51(ACTIVATE) → 0x51(FINALIZE).
    /// The two 0x51 packets were the missing piece — previous 3/4-packet replays got full ACKs
    /// yet the triggers stayed idle because the firmware never received the "apply" command.
    /// </summary>
    private void FullActivationSlot2_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🚀 发送完整激活序列 Slot 2 (6 packets)..."));
            var (ok, details) = _device.ReplayFullActivation(2);
            Dispatcher.Invoke(() => Log($"🚀 [Slot 2 完整激活] {(ok ? "OK" : "部分失败")}  {details}"));
            Dispatcher.Invoke(() => Log("   ⏱ 现在拉 LT 和 RT, 感受阻尼/扳机锁/振动"));
        });
    }

    /// <summary>
    /// Iterate all 4 captured slots with the FULL 6-packet activation sequence,
    /// 1200 ms between slots so the user has time to feel each configuration.
    /// </summary>
    private void FullActivationAllSlots_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🚀 开始轮询 4 个 Slot 的完整激活序列..."));
            for (int slot = 1; slot <= 4; slot++)
            {
                var (ok, details) = _device.ReplayFullActivation(slot);
                int s = slot;
                Dispatcher.Invoke(() => Log($"🚀 [Slot {s} 完整激活] {(ok ? "OK" : "失败")}  {details}"));
                Dispatcher.Invoke(() => Log($"   ⏱ 当前应是 Slot {s} 效果, 拉 LT/RT 感受 1.2s..."));
                Thread.Sleep(1200);
            }
            Dispatcher.Invoke(() => Log("✅ 轮询完成, 记住是哪个 Slot 让扳机有感觉"));
        });
    }

    /// <summary>
    /// Broadcast MAX strength rumble to every FlyDigi HID interface using 3 methods x 2 report formats.
    /// Unlike Slot 2 broadcast, rumble is highly perceivable regardless of ForceAdapt protocol correctness,
    /// so this isolates "interface+method reachable" from "ForceAdapt cmd correct".
    /// </summary>
    private void BroadcastStrongRumble_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🔌 临时断开主 HID 连接, 开始强振动广播..."));
            _device.Disconnect();
            Thread.Sleep(300);

            FlyDigiDevice.BroadcastStrongRumbleToAllInterfaces(
                perMethodMs: 1500,
                perInterfaceMs: 2500,
                perLineLog: line => Dispatcher.Invoke(() => Log(line)));

            Thread.Sleep(200);
            bool reconn = _device.TryReconnect();
            Dispatcher.Invoke(() => Log(reconn
                ? "🔌 强振动广播完成, 主 HID 已重连"
                : "❌ 强振动广播完成, 但主 HID 重连失败"));
        });
    }

    /// <summary>
    /// Broadcast a Slot 2 A4/A5/A6 sequence to every FlyDigi HID interface individually,
    /// using 3 different send mechanisms (HidSharp Write, HidD_SetOutputReport, SetFeature).
    /// User should pull LT and RT during each pause to feel which interface+method actually drives the hardware.
    /// IMPORTANT: disconnect our managed device first so the main interface isn't held open.
    /// </summary>
    private void BroadcastAllInterfaces_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            // Release our primary handle so enumeration can open every interface freely
            Dispatcher.Invoke(() => Log("🔌 临时断开主 HID 连接，开始广播..."));
            _device.Disconnect();
            Thread.Sleep(300);

            FlyDigiDevice.BroadcastSlot2ToAllInterfaces(
                interfaceDelayMs: 1500,
                perLineLog: line => Dispatcher.Invoke(() => Log(line)));

            // Reconnect main handle
            Thread.Sleep(200);
            bool reconn = _device.TryReconnect();
            Dispatcher.Invoke(() => Log(reconn
                ? "🔌 广播完成，主 HID 已重连"
                : "❌ 广播完成，但主 HID 重连失败"));
        });
    }

    /// <summary>
    /// Replay a single captured slot byte-for-byte.
    /// Tag="1".."4" on the button identifies which slot to send.
    /// </summary>
    private void ReplayCaptured_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (!int.TryParse(btn.Tag?.ToString(), out int slot)) return;
        _ = Task.Run(() =>
        {
            var (ok, details) = _device.ReplayCapturedSequence(slot);
            Dispatcher.Invoke(() => Log(
                $"🔁 [Replay Slot {slot}] {(ok ? "OK" : "失败")}  {details}"));
        });
    }

    /// <summary>
    /// Replay slots 1→2→3→4 in sequence with a 700 ms gap, so the user can feel
    /// which slot (if any) produces LT and which produces RT on the physical trigger.
    /// </summary>
    private void ReplayAllSlots_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            for (int s = 1; s <= 4; s++)
            {
                var (ok, details) = _device.ReplayCapturedSequence(s);
                Dispatcher.Invoke(() => Log(
                    $"🔁 [轮询 Slot {s}] {(ok ? "OK" : "失败")}  {details}"));
                Thread.Sleep(700);
            }
            Dispatcher.Invoke(() => Log("✅ 轮询完成，请回忆每个 Slot 期间 LT/RT 的反馈"));
        });
    }

    /// <summary>
    /// Send one probe: 0x11 SET_STATUS prefix + A4 + A5 with trigger-mapping override + A6.
    /// Tag encodes data[8]/data[9]: "0A0A" | "0A00" | "000A".
    /// </summary>
    private void ProbeTriggerMap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        string tag = btn.Tag?.ToString() ?? "0A0A";
        byte map8 = Convert.ToByte(tag.Substring(0, 2), 16);
        byte map9 = Convert.ToByte(tag.Substring(2, 2), 16);
        _ = Task.Run(() =>
        {
            var (ok, details) = _device.ReplayCapturedWithPrefix(
                slot: 2, map8: map8, map9: map9);
            Dispatcher.Invoke(() => Log(
                $"🎯 [探测 d8=0x{map8:X2} d9=0x{map9:X2}] {(ok ? "OK" : "失败")}  {details}"));
        });
    }

    /// <summary>
    /// Cycle through (0A,0A) → (0A,00) → (00,0A) with 1.2s gap between each,
    /// enough time to pull LT and RT separately and feel which mapping drives which.
    /// </summary>
    private void ProbeAllTriggerMaps_Click(object sender, RoutedEventArgs e)
    {
        (byte m8, byte m9, string label)[] probes =
        [
            (0x0A, 0x0A, "默认 d8=0A d9=0A"),
            (0x0A, 0x00, "仅 d8=0A (禁 d9)"),
            (0x00, 0x0A, "仅 d9=0A (禁 d8)"),
        ];
        _ = Task.Run(() =>
        {
            Dispatcher.Invoke(() => Log("🎯 开始 LT/RT 映射探测 (每组间隔 1.2s, 请拉 LT 和 RT)"));
            foreach (var (m8, m9, label) in probes)
            {
                var (ok, details) = _device.ReplayCapturedWithPrefix(
                    slot: 2, map8: m8, map9: m9);
                Dispatcher.Invoke(() => Log(
                    $"🎯 [{label}] {(ok ? "OK" : "失败")}  {details}"));
                Thread.Sleep(1200);
            }
            Dispatcher.Invoke(() => Log("✅ 探测完成, 请告诉我每组 LT/RT 分别的反馈"));
        });
    }

    private void AutoClear(int delayMs)
    {
        _ = Task.Delay(delayMs).ContinueWith(_ =>
        {
            if (!_isRunning)
            {
                Dispatcher.Invoke(() =>
                {
                    _device.ResetTriggers();
                    _device.SetForceAdaptEffect(ForceAdaptProtocol.ForceAdaptMode.Off, flags: 0x03);
                });
            }
        });
    }

    // ---- Helpers ----

    private void Log(string message)
    {
        // Thread-safe: dispatch to UI thread if needed
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        string entry = $"[{timestamp}] {message}";

        LogList.Items.Insert(0, entry);
        _logCount++;

        // Mirror to attached console (resizable + scrollable + copyable)
        if (_consoleAttached)
        {
            try { Console.WriteLine(entry); } catch { }
        }

        // Also write to file
        try { File.AppendAllText(_logFilePath, entry + "\n"); } catch { }

        // Limit log entries
        while (LogList.Items.Count > 200)
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    /// <summary>
    /// Attach a Win32 console window to this WPF process so the user gets a resizable,
    /// scrollable, copyable log view alongside the in-app ListBox. We also enlarge the
    /// screen-buffer to 9999 rows so long HID broadcast reports don't scroll off.
    /// Called once from the MainWindow ctor; subsequent calls are no-ops.
    /// </summary>
    private static void EnsureConsoleAttached()
    {
        if (_consoleAttached) return;
        try
        {
            if (!AllocConsole()) return;
            _consoleAttached = true;
            SetConsoleOutputCP(65001); // UTF-8, so Chinese + emoji render correctly
            SetConsoleTitle("StellarForceAdapt · 实时日志");

            // Redirect Console.Out to the newly attached console
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);

            // Enlarge the scrollback buffer
            nint h = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (GetConsoleScreenBufferInfo(h, out var info))
            {
                SetConsoleScreenBufferSize(h, new COORD { X = Math.Max((short)140, info.dwSize.X), Y = 9999 });
            }

            Console.WriteLine("=== StellarForceAdapt console attached (UTF-8, 9999 lines scrollback) ===");
        }
        catch { /* best-effort, never block app startup */ }
    }

    /// <summary>Open the log file with the system default editor so user can tail it.</summary>
    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log($"打开日志失败: {ex.Message}"); }
    }

    private void SetStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(status));
            return;
        }
        StatusText.Text = status;
    }
}
