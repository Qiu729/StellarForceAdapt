using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace StellarForceAdapt.Mapping;

public partial class RuleEditorPanel : UserControl
{
    private TriggerProfile? _profile;
    private MappingRule? _selectedRule;
    private bool _suppressUpdates;
    private bool _isDirty;

    private readonly List<ButtonFlag> _preconditionFlags;

    private static readonly (string Name, ushort Mask, bool IsTrigger)[] s_buttonDefs =
    [
        ("A", 0x1000, false), ("B", 0x2000, false), ("X", 0x4000, false), ("Y", 0x8000, false),
        ("LB", 0x0100, false), ("RB", 0x0200, false),
        ("LT", 0x0000, true), ("RT", 0x0000, true),
        ("↑", 0x0001, false), ("↓", 0x0002, false), ("←", 0x0004, false), ("→", 0x0008, false),
        ("Start", 0x0010, false), ("Back", 0x0020, false),
        ("L3", 0x0040, false), ("R3", 0x0080, false),
    ];

    public event EventHandler? ProfileSaved;

    public RuleEditorPanel()
    {
        InitializeComponent();

        _preconditionFlags = s_buttonDefs.Select(d => new ButtonFlag { Name = d.Name, Mask = d.Mask, IsTrigger = d.IsTrigger }).ToList();

        foreach (var f in _preconditionFlags) f.Changed += OnButtonFlagChanged;

        PreconditionList.ItemsSource = _preconditionFlags;

        // Populate combo boxes
        EffectTypeCombo.ItemsSource = Enum.GetValues<EffectType>();
        EffectModeCombo.ItemsSource = new[] { "racing", "machinegun", "sniper", "triggerlock", "vibrate", "off" };
        EffectTargetCombo.ItemsSource = Enum.GetValues<TriggerTarget>();
    }

    public void LoadProfile(TriggerProfile profile)
    {
        _profile = profile;
        _isDirty = false;
        _selectedRule = null;
        RefreshRuleList();
    }

    private void RefreshRuleList()
    {
        RuleListBox.ItemsSource = null;
        RuleListBox.ItemsSource = _profile?.Rules;
        EmptyRulesHint.Visibility = (_profile?.Rules.Count ?? 0) == 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (_profile?.Rules.Count > 0)
        {
            RuleListBox.SelectedIndex = 0;
        }
        else
        {
            EditorForm.Visibility = Visibility.Collapsed;
        }
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUpdates) return;

        if (_isDirty && _selectedRule != null)
        {
            var result = MessageBox.Show("当前规则有未保存的修改，是否放弃？", "未保存修改",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                _suppressUpdates = true;
                RuleListBox.SelectedItem = _selectedRule;
                _suppressUpdates = false;
                return;
            }
        }

        if (RuleListBox.SelectedItem is MappingRule rule)
        {
            _selectedRule = rule;
            _isDirty = false;
            PopulateForm(rule);
            EditorForm.Visibility = Visibility.Visible;
        }
        else
        {
            _selectedRule = null;
            EditorForm.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateForm(MappingRule rule)
    {
        _suppressUpdates = true;

        var cond = rule.Condition;
        var eff = rule.Effect;

        // Precondition flags
        SetPreconditionFlags(cond);

        // Trigger sliders
        LtMinSlider.Value = cond.LeftTriggerMin;
        LtMaxSlider.Value = cond.LeftTriggerMax;
        RtMinSlider.Value = cond.RightTriggerMin;
        RtMaxSlider.Value = cond.RightTriggerMax;
        LtMinLabel.Text = cond.LeftTriggerMin.ToString();
        LtMaxLabel.Text = cond.LeftTriggerMax.ToString();
        RtMinLabel.Text = cond.RightTriggerMin.ToString();
        RtMaxLabel.Text = cond.RightTriggerMax.ToString();

        // Stick sliders
        LeftStickSlider.Value = cond.LeftStickMagnitudeMin;
        RightStickSlider.Value = cond.RightStickMagnitudeMin;
        LeftStickLabel.Text = cond.LeftStickMagnitudeMin.ToString();
        RightStickLabel.Text = cond.RightStickMagnitudeMin.ToString();

        // Effect fields
        EffectTypeCombo.SelectedItem = eff.Type;
        EffectModeCombo.SelectedItem = eff.Mode?.ToLowerInvariant() ?? "racing";
        EffectTargetCombo.SelectedItem = eff.Target;

        IntensitySlider.Value = eff.Intensity;
        SpeedSlider.Value = eff.Speed;
        IntensityLabel.Text = eff.Intensity.ToString();
        SpeedLabel.Text = eff.Speed.ToString();

        DurationBox.Text = eff.DurationMs.ToString();
        PriorityBox.Text = rule.Priority.ToString();
        CooldownBox.Text = rule.CooldownMs.ToString();

        _suppressUpdates = false;
    }

    private void SetPreconditionFlags(TriggerCondition cond)
    {
        foreach (var f in _preconditionFlags)
        {
            if (f.IsTrigger)
                f.IsChecked = f.Name == "LT" ? cond.PreconditionLeftTrigger : cond.PreconditionRightTrigger;
            else
                f.IsChecked = (cond.PreconditionButtons & f.Mask) != 0;
        }
    }

    private void OnButtonFlagChanged(object? sender, EventArgs e)
    {
        if (_suppressUpdates || _selectedRule == null || sender is not ButtonFlag flag) return;

        if (flag.IsTrigger)
        {
            if (flag.Name == "LT")
                _selectedRule.Condition.PreconditionLeftTrigger = flag.IsChecked;
            else if (flag.Name == "RT")
                _selectedRule.Condition.PreconditionRightTrigger = flag.IsChecked;
        }
        else
        {
            ushort mask = 0;
            foreach (var f in _preconditionFlags)
                if (f.IsChecked && !f.IsTrigger)
                    mask |= f.Mask;
            _selectedRule.Condition.PreconditionButtons = mask;
        }

        _isDirty = true;
    }

    private void TriggerSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUpdates || _selectedRule == null) return;
        _selectedRule.Condition.LeftTriggerMin = (byte)LtMinSlider.Value;
        _selectedRule.Condition.LeftTriggerMax = (byte)LtMaxSlider.Value;
        _selectedRule.Condition.RightTriggerMin = (byte)RtMinSlider.Value;
        _selectedRule.Condition.RightTriggerMax = (byte)RtMaxSlider.Value;
        LtMinLabel.Text = ((byte)LtMinSlider.Value).ToString();
        LtMaxLabel.Text = ((byte)LtMaxSlider.Value).ToString();
        RtMinLabel.Text = ((byte)RtMinSlider.Value).ToString();
        RtMaxLabel.Text = ((byte)RtMaxSlider.Value).ToString();
        _isDirty = true;
    }

    private void StickSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUpdates || _selectedRule == null) return;
        _selectedRule.Condition.LeftStickMagnitudeMin = (short)LeftStickSlider.Value;
        _selectedRule.Condition.RightStickMagnitudeMin = (short)RightStickSlider.Value;
        LeftStickLabel.Text = ((short)LeftStickSlider.Value).ToString();
        RightStickLabel.Text = ((short)RightStickSlider.Value).ToString();
        _isDirty = true;
    }

    private void EffectSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressUpdates || _selectedRule == null) return;
        _selectedRule.Effect.Intensity = (byte)IntensitySlider.Value;
        _selectedRule.Effect.Speed = (byte)SpeedSlider.Value;
        IntensityLabel.Text = ((byte)IntensitySlider.Value).ToString();
        SpeedLabel.Text = ((byte)SpeedSlider.Value).ToString();
        _isDirty = true;
    }

    private void EffectType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUpdates || _selectedRule == null) return;
        if (EffectTypeCombo.SelectedItem is EffectType type)
        {
            _selectedRule.Effect.Type = type;
            _isDirty = true;
        }
    }

    private void MarkDirty(object sender, EventArgs e)
    {
        if (_suppressUpdates || _selectedRule == null) return;

        // Sync text fields
        if (sender == DurationBox && int.TryParse(DurationBox.Text, out int d))
            _selectedRule.Effect.DurationMs = d;
        if (sender == PriorityBox && int.TryParse(PriorityBox.Text, out int p))
            _selectedRule.Priority = p;
        if (sender == CooldownBox && int.TryParse(CooldownBox.Text, out int c))
            _selectedRule.CooldownMs = c;

        // Sync combos
        if (sender == EffectModeCombo && EffectModeCombo.SelectedItem is string mode)
            _selectedRule.Effect.Mode = mode;
        if (sender == EffectTargetCombo && EffectTargetCombo.SelectedItem is TriggerTarget target)
            _selectedRule.Effect.Target = target;

        _isDirty = true;
    }

    private void RuleItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is MappingRule rule)
        {
            RuleListBox.SelectedItem = rule;
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        var rule = new MappingRule
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = $"规则 {_profile.Rules.Count + 1}",
            Priority = 100,
            Condition = new TriggerCondition(),
            Effect = new TriggerEffect
            {
                Type = EffectType.ForceAdapt,
                Mode = "racing",
                Target = TriggerTarget.Both,
                Intensity = 128,
                Speed = 128,
            }
        };

        _profile.Rules.Add(rule);
        RefreshRuleList();
        RuleListBox.SelectedItem = rule;
        _isDirty = true;
        LogToParent("新建规则");
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null || _selectedRule == null) return;
        if (_profile.Rules.Count <= 1)
        {
            MessageBox.Show("至少保留一条规则", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int idx = _profile.Rules.IndexOf(_selectedRule);
        _profile.Rules.Remove(_selectedRule);
        _isDirty = true;
        RefreshRuleList();
        LogToParent("删除规则");

        // Select adjacent rule
        if (_profile.Rules.Count > 0)
        {
            RuleListBox.SelectedIndex = Math.Min(idx, _profile.Rules.Count - 1);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is MappingRule rule && _profile != null)
            MoveRule(rule, -1);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is MappingRule rule && _profile != null)
            MoveRule(rule, 1);
    }

    private void MoveRule(MappingRule rule, int delta)
    {
        int idx = _profile!.Rules.IndexOf(rule);
        int newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _profile.Rules.Count) return;

        _profile.Rules.RemoveAt(idx);
        _profile.Rules.Insert(newIdx, rule);
        _isDirty = true;
        RefreshRuleList();
        RuleListBox.SelectedItem = rule;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;

        // Sync any pending text edits
        SyncTextFields();

        string? path = _profile.FilePath ?? PromptSavePath();
        if (string.IsNullOrEmpty(path)) return;

        _profile.FilePath = path;
        _profile.Save(path);
        _isDirty = false;
        ProfileSaved?.Invoke(this, EventArgs.Empty);
        LogToParent($"配置已保存: {Path.GetFileName(path)}");
    }

    private void SyncTextFields()
    {
        if (_selectedRule == null) return;
        if (int.TryParse(DurationBox.Text, out int d))
            _selectedRule.Effect.DurationMs = d;
        if (int.TryParse(PriorityBox.Text, out int p))
            _selectedRule.Priority = p;
        if (int.TryParse(CooldownBox.Text, out int c))
            _selectedRule.CooldownMs = c;
    }

    private static string? PromptSavePath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = "my_profile.json",
            Title = "保存配置文件",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void LogToParent(string message)
    {
        // Walk up visual tree to find MainWindow and call Log
        DependencyObject? parent = this;
        while (parent != null)
        {
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            if (parent is MainWindow mw)
            {
                mw.LogMessage(message);
                break;
            }
        }
    }
}

/// <summary>
/// Represents a single XInput button for checkbox binding.
/// </summary>
public class ButtonFlag : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public ushort Mask { get; set; }
    public bool IsTrigger { get; set; }

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsChecked)));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? Changed;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
