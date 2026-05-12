using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private readonly StellarBladeMonitor _gameMonitor = new();
    private readonly MappingEngine _engine;
    private readonly CancellationTokenSource _uiCts = new();
    private ControllerMapping _mapping = new();

    private bool _isRunning;
    private int _logCount;
    private readonly List<(string Path, TriggerProfile Profile)> _profiles = [];
    private readonly string _profilesDir;
    private readonly string _mappingPath;
    private readonly string _logFilePath;

    // Binding state (disabled in XInput mode)

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

        _engine = new MappingEngine(_device, _gameMonitor);

        // Wire diagnostics to log file
        FlyDigiDevice.Log = msg => Log(msg);

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
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiCts.Cancel();
        _engine.Stop();
        _device.Dispose();
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
        var state = _engine.CurrentInput;
        if (!state.Connected) return;

        LeftTriggerBar.Value = state.LeftTrigger;
        RightTriggerBar.Value = state.RightTrigger;
        LeftTriggerValue.Text = state.LeftTrigger.ToString();
        RightTriggerValue.Text = state.RightTrigger.ToString();
    }

    private void UpdateStatusBar()
    {
        var state = _engine.CurrentInput;
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
        Dispatcher.Invoke(() =>
        {
            SetStatus(status);
            if (status.StartsWith("HID") || status.Contains("降级") || status.Contains("Profile"))
                Log($"ℹ️ {status}");
        });
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
            // Log all detected FlyDigi HID interfaces for diagnostics
            foreach (var d in devices)
                Log($"📡 HID接口: {d.ProductName} PID=0x{d.ProductId:X4} OutLen={d.OutputReportLength} InLen={d.InputReportLength}");

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

            if (cd2Ok)
                Log($"✅ 手柄连接成功 (PID=0x{_device.ProductId:X4}, OutLen={_device.OutputReportLength})");
            else
                Log("⚠️ 手柄连接失败，请确认已通过 USB 连接");
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
        var state = _engine.CurrentInput;
        if (!state.Connected) return;

        RawHidText.Text = $"Buttons: 0x{state.Buttons:X4}";
        RawChangesText.Text = $"LT:{state.LeftTrigger} RT:{state.RightTrigger} "
            + $"LStick:({state.LeftThumbX},{state.LeftThumbY}) "
            + $"RStick:({state.RightThumbX},{state.RightThumbY})";

        SetButtonLight("A", state);
        SetButtonLight("B", state);
        SetButtonLight("X", state);
        SetButtonLight("Y", state);
        SetButtonLight("LB", state);
        SetButtonLight("RB", state);
        SetButtonLight("LT", state);
        SetButtonLight("RT", state);
    }

    private void SetButtonLight(string name, XInputState state)
    {
        var border = FindName($"Btn_{name}") as System.Windows.Controls.Border;
        if (border == null) return;

        bool pressed = name switch
        {
            "A" => (state.Buttons & 0x1000) != 0,
            "B" => (state.Buttons & 0x2000) != 0,
            "X" => (state.Buttons & 0x4000) != 0,
            "Y" => (state.Buttons & 0x8000) != 0,
            "LB" => (state.Buttons & 0x0100) != 0,
            "RB" => (state.Buttons & 0x0200) != 0,
            "LT" => state.LeftTrigger > 30,
            "RT" => state.RightTrigger > 30,
            _ => false,
        };

        border.Background = pressed
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(51, 51, 51));
    }

    // ---- Button Binding ----

    private void BindButton_Click(object sender, RoutedEventArgs e)
    {
        Log("⚠️ XInput 模式不支持自定义按键绑定");
    }

    private void CheckBinding()
    {
        // XInput mode does not support custom button binding
    }

    private void SaveMapping_Click(object sender, RoutedEventArgs e)
    {
        Log("⚠️ XInput 模式不支持自定义按键绑定");
    }

    private void ResetMapping_Click(object sender, RoutedEventArgs e)
    {
        Log("⚠️ XInput 模式不支持自定义按键绑定");
    }

    // ---- Manual Test ----

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
