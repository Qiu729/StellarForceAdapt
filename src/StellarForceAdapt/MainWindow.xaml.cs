using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StellarForceAdapt.HID;
using StellarForceAdapt.Mapping;

namespace StellarForceAdapt;

public partial class MainWindow : Window
{
    private readonly FlyDigiDevice _device = new();
    private readonly MappingEngine _engine;
    private readonly List<(string Path, TriggerProfile Profile)> _profiles = [];
    private readonly string _profilesDir;

    private bool _isRunning;
    private bool _isReconnecting;
    private int _logCount;

    public MainWindow()
    {
        InitializeComponent();

        var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var baseDir = Directory.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles"))
            ? AppDomain.CurrentDomain.BaseDirectory
            : exeDir ?? AppDomain.CurrentDomain.BaseDirectory;
        _profilesDir = Path.Combine(baseDir, "Profiles");

        _engine = new MappingEngine(_device);

        _device.ConnectionChanged += OnControllerConnectionChanged;
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.EffectTriggered += OnEffectTriggered;

        RefreshProfiles_Click(null!, null!);
        ScanController();
        StartUiTimer();

        System.Diagnostics.Debug.WriteLine("[UI] Initialized");
    }

    protected override void OnClosed(EventArgs e)
    {
        _engine.Stop();
        _device.Dispose();
        _engine.Dispose();
        base.OnClosed(e);
    }

    private void StartUiTimer()
    {
        var timer = new System.Timers.Timer(50);
        timer.Elapsed += (_, _) =>
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateTriggerDisplay();
                    UpdateStatusBar();
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

        ControllerStatus.Background = connected
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : (SolidColorBrush)FindResource("BgMedium");
        ControllerText.Text = connected
            ? $"\U0001F3AE 手柄: 已连接 ({_device.DeviceName ?? "FlyDigi"})"
            : "\U0001F3AE 手柄: 未连接";

        EngineStatus.Background = _isRunning
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80))
            : new SolidColorBrush(Color.FromRgb(244, 67, 54));
        EngineText.Text = _isRunning ? "⚙ 引擎: 运行中" : "⚙ 引擎: 停止";
    }

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
                if (_isReconnecting) return;
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

    private void OnEngineStatusChanged(object? sender, string status)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus(status);
            if (status.Contains("Profile"))
                Log($"ℹ {status}");
        });
    }

    private void OnEffectTriggered(object? sender, string effectName)
    {
        Dispatcher.Invoke(() => Log($"⚡ 触发: {effectName}"));
    }

    private void ToggleEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
            StopEngine();
        else
            StartEngine();
    }

    private void StartEngine()
    {
        if (!_device.IsConnected)
        {
            if (!_device.Connect())
            {
                MessageBox.Show("无法连接到手柄，请确保 FlyDigi 手柄已通过 USB 连接", "连接失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (ProfileCombo.SelectedItem is TriggerProfile profile)
            _engine.SetProfile(profile);
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
            foreach (var d in devices)
                Log($"\U0001F4E1 HID接口: {d.ProductName} PID=0x{d.ProductId:X4}");

            var known = devices.FirstOrDefault(d => d.IsKnown);
            if (known != null)
                Log($"\U0001F4E1 检测到飞智手柄: {known.ProductName} (PID=0x{known.ProductId:X4})");

            bool ok = _device.Connect();
            if (ok)
                Log($"✅ 手柄连接成功 (PID=0x{_device.ProductId:X4})");
            else
                Log("⚠ 手柄连接失败，请确认已通过 USB 连接");
        }
        else
            Log("\U0001F50D 未检测到飞智手柄");
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
            Log($"\U0001F4C2 已加载 {loaded.Count} 个配置文件");
        }
        else
            Log("⚠ 未找到配置文件");
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

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }

        string entry = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        LogList.Items.Insert(0, entry);
        _logCount++;

        while (LogList.Items.Count > 200)
            LogList.Items.RemoveAt(LogList.Items.Count - 1);
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
