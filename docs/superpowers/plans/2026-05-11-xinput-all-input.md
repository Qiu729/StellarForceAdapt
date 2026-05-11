# XInput 全输入层迁移 — 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将全部游戏手柄输入（按钮+扳机+摇杆）从 HID 迁移到 XInput，解决双扳机同时按下时 HID 差值编码导致的位置丢失问题。

**Architecture:** 删除 HIDGamepadReader，MappingEngine 始终使用 XInputWatcher。StellarBladeMonitor 仅保留 UpdateFromXInput。MainWindow 通过 MappingEngine 暴露的 XInput 状态更新 UI。

**Tech Stack:** C# .NET 9, WPF, XInput (xinput1_4.dll via P/Invoke), HidSharp (仅保留命令下发)

---

### Task 1: 删除 HIDGamepadReader

**Files:**
- Delete: `src/StellarForceAdapt/Monitor/HIDGamepadReader.cs`

- [ ] **Step 1: 删除文件**

```bash
rm src/StellarForceAdapt/Monitor/HIDGamepadReader.cs
```

- [ ] **Step 2: 提交**

```bash
git add src/StellarForceAdapt/Monitor/HIDGamepadReader.cs
git commit -m "feat: remove HIDGamepadReader — migrating all input to XInput"
```

---

### Task 2: 清理 StellarBladeMonitor — 移除 UpdateFromHID

**Files:**
- Modify: `src/StellarForceAdapt/Monitor/StellarBladeMonitor.cs`

- [ ] **Step 1: 删除 UpdateFromHID 方法**

删除第 48-96 行的 `UpdateFromHID` 方法（整个方法包括注释）。

- [ ] **Step 2: 删除不再需要的 using**

删除第 2 行的 `using StellarForceAdapt.Mapping;`（UpdateFromHID 是唯一使用 ControllerMapping 的消费者）。

- [ ] **Step 3: 构建验证**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

预期：编译失败（MappingEngine 仍引用 HIDGamepadReader）。

- [ ] **Step 4: 提交**

```bash
git add src/StellarForceAdapt/Monitor/StellarBladeMonitor.cs
git commit -m "feat: remove StellarBladeMonitor.UpdateFromHID"
```

---

### Task 3: 重构 MappingEngine — 移除 HID，暴露 XInput

**Files:**
- Modify: `src/StellarForceAdapt/Mapping/MappingEngine.cs`

- [ ] **Step 1: 移除 HID 相关字段和导入**

删除：
- `using StellarForceAdapt.Monitor;`（第4行 HID import，保留其他）
- `private readonly HIDGamepadReader _gamepad;` 字段（第13行）
- `private bool _xinputActive;` 字段（第28行）

- [ ] **Step 2: 重构构造函数**

当前（第47-57行）：
```csharp
public MappingEngine(FlyDigiDevice device, HIDGamepadReader gamepad, StellarBladeMonitor gameMonitor)
{
    _device = device;
    _gamepad = gamepad;
    _gameMonitor = gameMonitor;

    _gamepad.StateChanged += OnGamepadStateChanged;
    _gameMonitor.GameStateChanged += OnGameStateChanged;
    _gameMonitor.GameProcessChanged += OnGameProcessChanged;
    _xinput.StateChanged += OnXInputStateChanged;
}
```

改为：
```csharp
public MappingEngine(FlyDigiDevice device, StellarBladeMonitor gameMonitor)
{
    _device = device;
    _gameMonitor = gameMonitor;

    _gameMonitor.GameStateChanged += OnGameStateChanged;
    _gameMonitor.GameProcessChanged += OnGameProcessChanged;
    _xinput.StateChanged += OnXInputStateChanged;
}
```

- [ ] **Step 3: 暴露 XInput 状态**

新增属性（紧接 `IsRunning` 属性之后）：
```csharp
public XInputState CurrentInput => _xinput.CurrentState;
```

- [ ] **Step 4: 删除 OnGamepadStateChanged 方法**

删除整个 `OnGamepadStateChanged` 方法（第346-351行）：
```csharp
private void OnGamepadStateChanged(object? sender, HIDGamepadState state)
{
    if (!_gameMonitor.IsGameRunning || ButtonMapping == null) return;
    _gameMonitor.UpdateFromHID(state, ButtonMapping);
}
```

- [ ] **Step 5: 简化 Start() 方法**

当前（第69-102行）替换为：
```csharp
public void Start()
{
    if (_running) return;
    _running = true;

    _xinput.Start(4);
    _gameMonitor.Start();

    _engineThread = new Thread(EngineLoop)
    {
        IsBackground = true,
        Name = "Mapping-Engine"
    };
    _engineThread.Start();

    StatusChanged?.Invoke(this, "XInput 输入已启用");
    StatusChanged?.Invoke(this, "Engine started");
    Debug.WriteLine("[Engine] Started");
}
```

- [ ] **Step 6: 简化 Stop() 方法**

当前（第104-117行）替换为：
```csharp
public void Stop()
{
    _running = false;
    _xinput.Stop();
    _gameMonitor.Stop();

    _device.ResetTriggers();
    _activeForceAdapt = null;

    StatusChanged?.Invoke(this, "Engine stopped");
    Debug.WriteLine("[Engine] Stopped");
}
```

- [ ] **Step 7: 简化 Dispose()**

当前（第119-129行）替换为：
```csharp
public void Dispose()
{
    Stop();
    _cts.Cancel();
    _cts.Dispose();
    _gameMonitor.GameStateChanged -= OnGameStateChanged;
    _gameMonitor.GameProcessChanged -= OnGameProcessChanged;
    _xinput.StateChanged -= OnXInputStateChanged;
    _xinput.Dispose();
}
```

- [ ] **Step 8: 删除不再需要的 using**

删除第 3 行的 `using StellarForceAdapt.Monitor;`（HIDGamepadState 和 HIDGamepadReader 不再使用），但保留它因为 XInputWatcher 和 StellarBladeMonitor 仍在同一命名空间。检查是否还需要 — 是的，`StellarBladeMonitor` 和 `XInputWatcher` 都在 `StellarForceAdapt.Monitor` 命名空间。

保留 `using StellarForceAdapt.Monitor;`。

- [ ] **Step 9: 删除 ButtonMapping 属性（不再使用）**

删除：
```csharp
public ControllerMapping? ButtonMapping { get; set; }
```

（OnGamepadStateChanged 是唯一使用它来传给 UpdateFromHID 的地方，均已删除。）

- [ ] **Step 10: 构建验证**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

预期：MainWindow 编译失败（构造函数参数变更、_gamepad 引用失效）。

- [ ] **Step 11: 提交**

```bash
git add src/StellarForceAdapt/Mapping/MappingEngine.cs
git commit -m "feat: refactor MappingEngine to always use XInput, expose CurrentInput"
```

---

### Task 4: 更新 MainWindow — 适配 XInput

**Files:**
- Modify: `src/StellarForceAdapt/MainWindow.xaml.cs`

- [ ] **Step 1: 删除 _gamepad 字段和相关引用**

删除：
- `private readonly HIDGamepadReader _gamepad = new();`（第30行）
- `HIDGamepadReader.Log = msg => Log(msg);`（第80行）
- `_gamepad.Dispose();`（第108行）

- [ ] **Step 2: 移除 HID 连接检查，简化 ScanController**

当前 `ScanController()` 中的 HID 连接部分（第352-364行）：
```csharp
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
```

改为：
```csharp
bool cd2Ok = _device.Connect();

if (cd2Ok)
    Log("✅ 手柄连接成功 (CD2)");
else
    Log("⚠️ 手柄连接失败，请确认已通过 USB 连接");
```

- [ ] **Step 3: 映射 XInput 按钮到 Debug 灯**

将 `SetButtonLight` 方法（第458-479行）改为使用 XInput 位掩码：

```csharp
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
```

- [ ] **Step 4: 更新 UpdateTriggerDisplay — 使用 XInput**

当前（第135-143行）：
```csharp
private void UpdateTriggerDisplay()
{
    var state = _gamepad.CurrentState;
    if (!state.Connected) return;

    LeftTriggerBar.Value = state.LeftTrigger;
    RightTriggerBar.Value = state.RightTrigger;
    LeftTriggerValue.Text = state.LeftTrigger.ToString();
    RightTriggerValue.Text = state.RightTrigger.ToString();
}
```

改为：
```csharp
private void UpdateTriggerDisplay()
{
    var state = _engine.CurrentInput;
    if (!state.Connected) return;

    LeftTriggerBar.Value = state.LeftTrigger;
    RightTriggerBar.Value = state.RightTrigger;
    LeftTriggerValue.Text = state.LeftTrigger.ToString();
    RightTriggerValue.Text = state.RightTrigger.ToString();
}
```

- [ ] **Step 5: 更新 UpdateStatusBar — 使用 XInput**

当前（第146-171行）中的 `_gamepad.CurrentState`（第148行）改为 `_engine.CurrentInput`。

- [ ] **Step 6: 更新 UpdateDebugDisplay — 使用 XInput**

当前方法（第418-456行）重写为 XInput 版本：
```csharp
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
```

- [ ] **Step 7: 更新 MappingEngine 构造函数调用**

第 74 行：
```csharp
_engine = new MappingEngine(_device, _gamepad, _gameMonitor)
{
    ButtonMapping = _mapping
};
```

改为：
```csharp
_engine = new MappingEngine(_device, _gameMonitor);
```

- [ ] **Step 8: 禁用按钮绑定功能**

删除 `_idleState`、`_bindingIdle`、`_bindingTarget` 字段及所有绑定相关代码。`BindButton_Click`、`CheckBinding`、`SaveMapping_Click`、`ResetMapping_Click` 方法内容替换为 `Log("⚠️ XInput 模式不支持自定义按键绑定");`。

`_mappingPath`、`_mapping` 字段和相关加载/保存代码保留（不影响功能）。

- [ ] **Step 9: 构建验证**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

预期：编译成功。

- [ ] **Step 10: 提交**

```bash
git add src/StellarForceAdapt/MainWindow.xaml.cs
git commit -m "feat: adapt MainWindow UI to XInput input source"
```

---

### Task 5: 构建和验证

**Files:**
- No file changes — 验证步骤。

- [ ] **Step 1: 完整构建**

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

预期：Build succeeded. 0 Error(s).

- [ ] **Step 2: 检查是否有遗留的 HIDGamepadReader 引用**

```bash
grep -r "HIDGamepadReader" src/StellarForceAdapt/
grep -r "UpdateFromHID" src/StellarForceAdapt/
grep -r "_gamepad" src/StellarForceAdapt/
```

预期：无匹配结果（或仅在 dead code 注释中）。

- [ ] **Step 3: 提交**

```bash
git commit --allow-empty -m "chore: verify XInput migration — no HID input references remain"
```

---

### Task 6: 空状态测试（需手柄硬件）

> 以下步骤需要八爪鱼5手柄连接。如无硬件，跳过本任务。

- [ ] **Step 1: 运行程序**

```bash
dotnet run --project src/StellarForceAdapt/StellarForceAdapt.csproj
```

- [ ] **Step 2: 验证空闲状态**
  - 不按任何按键，触发条应显示 LT=0, RT=0
  - 按钮灯全灭

- [ ] **Step 3: 验证单扳机按下**
  - 按 LT：触发条显示对应值，按钮灯亮
  - 按 RT：同上

- [ ] **Step 4: 验证双扳机同时按下**
  - 同时按 LT 和 RT，两个触发条应各自显示正确的独立位置
  - 两个灯同时亮

- [ ] **Step 5: 提交（如无变更则跳过）**

```bash
git commit --allow-empty -m "test: verify dual-trigger independent reading via XInput"
```
