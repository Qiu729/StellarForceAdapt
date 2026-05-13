# StellarForceAdapt

飞智 (FlyDigi) 手柄 ForceAdapt 自适应扳机通用适配器。

通过 USB HID 直连飞智手柄，下发 ForceAdapt 命令实现 6 种扳机力反馈模式。配备可视化规则编辑器，支持基于 XInput 输入（扳机行程、按键、摇杆）实时响应切换力反馈模式。

## 功能

- **6 种力反馈模式**：Off / Racing / Machinegun / Sniper / TriggerLock / Vibration
- **XInput 输入捕获**：双扳机 + 按键 + 摇杆，~200Hz 轮询
- **可视化规则编辑器**：图形化配置扳机触发规则，实时生效
- **前置条件系统**：支持按键/LT/RT 作为前置条件，灵活组合触发逻辑
- **左右扳机独立控制**：各自独立的模式和状态追踪，支持不同效果
- **自动重连**：手柄断开后自动重试连接
- **暗色主题 UI**：现代化 WPF 界面，自适应布局

## 下载

从 [Releases](https://github.com/Qiu729/StellarForceAdapt/releases) 页面下载最新版本的 `StellarForceAdapt-vX.X.X.zip`，解压即可使用。

## 支持手柄

| 型号 | PID |
|------|------|
| 飞智八爪鱼 5 (APEX 5) | 0x2501 |
| 飞智 Vader 4 Pro | 0x2012 |
| 飞智 APEX 4 | 0x2021 / 0x2023 |
| 飞智 Vader 3 Pro | 0x2011 |
| 飞智 Vader 3 | 0x2010 |

## 快速开始

1. 从 [Releases](https://github.com/Qiu729/StellarForceAdapt/releases) 下载最新版压缩包并解压
2. 通过 USB 连接飞智手柄（确保手柄已开启）
3. 运行 `StellarForceAdapt.exe`
4. 点击 **"扫描手柄"**，等待状态栏显示 "手柄已连接"
5. 在配置文件下拉框中选择一个配置（如 `示例配置`）
6. 点击 **"启动引擎"**，扣动扳机即可体验力反馈效果

![主界面布局](docs/screenshots/main-window.png)

### 界面说明

主界面分为三个区域：

- **左侧面板**：配置文件选择、扳机状态实时显示（左右扳机行程条）、事件日志
- **右侧面板**：可视化规则编辑器，用于创建和修改扳机触发规则
- **顶部状态栏**：手柄连接状态、引擎运行状态、启动/停止按钮

## 构建

### 环境要求

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10+（依赖 `xinput1_4.dll`）

### 构建命令

```bash
# 克隆仓库
git clone https://github.com/Qiu729/StellarForceAdapt.git
cd StellarForceAdapt

# 还原依赖并构建
dotnet restore
dotnet build -c Release

# 发布为单文件（可选）
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

构建产物位于 `src/StellarForceAdapt/bin/Release/net9.0-windows/`。

### 发布脚本

也可直接运行打包脚本生成发布包：

```bash
tools/build-release.bat
```

## 配置扳机规则

StellarForceAdapt 使用 JSON 配置文件定义扳机触发规则，也可以通过内置的 **可视化规则编辑器** 直接操作。

### 配置文件

配置文件位于程序目录下的 `Profiles/` 文件夹，格式为 JSON。程序启动时自动加载该目录下所有有效配置。

示例 `Profiles/example.json`：

```json
{
  "name": "示例配置",
  "version": "1.0",
  "description": "RT 按下时机枪反馈，LT 按下时赛车阻尼",
  "rules": [
    {
      "id": "rt_mg",
      "name": "RT 机枪反馈",
      "priority": 100,
      "cooldown_ms": 0,
      "condition": {
        "precondition_buttons": 0,
        "precondition_left_trigger": false,
        "precondition_right_trigger": false,
        "left_trigger_min": 0,
        "left_trigger_max": 255,
        "right_trigger_min": 30,
        "right_trigger_max": 255,
        "left_stick_magnitude_min": 0,
        "right_stick_magnitude_min": 0
      },
      "effect": {
        "type": "force_adapt",
        "mode": "machinegun",
        "target": "right",
        "duration_ms": 0,
        "intensity": 220,
        "speed": 100
      }
    }
  ]
}
```

### 规则字段说明

| 字段 | 类型 | 说明 |
|------|------|------|
| `id` | string | 规则唯一标识 |
| `name` | string | 规则名称（显示在编辑器中） |
| `priority` | int | 优先级，数值越大越优先。多个规则同时满足时取最高优先级 |
| `cooldown_ms` | int | 冷却时间（毫秒），冷却期内规则不会重复触发 |

### 前置条件 (Preconditions)

前置条件是规则的"准入检查"，所有前置条件满足后才会进一步判断扳机行程范围。这是比行程范围更优先的过滤层。

| 字段 | 类型 | 说明 |
|------|------|------|
| `precondition_buttons` | ushort | XInput 按键掩码，指定的按键**全部按下**时才继续判断。0=不检查 |
| `precondition_left_trigger` | bool | 左扳机(LT)非零时才继续判断 |
| `precondition_right_trigger` | bool | 右扳机(RT)非零时才继续判断 |

例如，设置 `precondition_buttons` 为 LB 掩码(0x0100) + `precondition_right_trigger = true`，意味着只有按住 LB + RT 时这条规则才可能触发。

### 扳机行程条件

前置条件通过后，使用以下字段精确控制触发区间：

| 字段 | 类型 | 说明 |
|------|------|------|
| `left_trigger_min` | byte | 左扳机最小行程 (0-255) |
| `left_trigger_max` | byte | 左扳机最大行程 (0-255) |
| `right_trigger_min` | byte | 右扳机最小行程 (0-255) |
| `right_trigger_max` | byte | 右扳机最大行程 (0-255) |
| `left_stick_magnitude_min` | short | 左摇杆矢量幅度下限 (0-32768) |
| `right_stick_magnitude_min` | short | 右摇杆矢量幅度下限 (0-32768) |

行程默认值：min=0, max=255 表示不限制该扳机。

### 效果配置

| 字段 | 类型 | 说明 |
|------|------|------|
| `type` | string | `force_adapt` / `rumble` / `sequence` |
| `mode` | string | 力反馈模式（见下表） |
| `target` | string | 目标扳机: `left` / `right` / `both` |
| `duration_ms` | int | 持续时间（毫秒）。0=持续生效直到条件不满足 |
| `intensity` | byte | 力度 (0-255)，rumble 模式下为震动强度 |
| `speed` | byte | 速度 (0-255) |

#### 力反馈模式

| mode | 效果 |
|------|------|
| `off` | 关闭力反馈，扳机恢复自由行程 |
| `racing` | 赛车阻尼，线性阻力适合油门模拟 |
| `machinegun` | 机枪振动，连续高频振动 |
| `sniper` | 狙击枪阻力，阶段性阻尼 |
| `triggerlock` | 扳机锁，锁定在固定位置 |
| `vibrate` | 持续振动模式 |

#### 效果类型

| type | 说明 |
|------|------|
| `force_adapt` | ForceAdapt 扳机力反馈（需指定 mode） |
| `rumble` | 扳机震动马达（需指定 intensity） |
| `sequence` | 时序效果序列（需指定 sequence 数组，按顺序播放） |

### XInput 按键掩码参考

| 按键 | 掩码 | | 按键 | 掩码 |
|------|------|-|------|------|
| A | 0x1000 | | LB | 0x0100 |
| B | 0x2000 | | RB | 0x0200 |
| X | 0x4000 | | Start | 0x0010 |
| Y | 0x8000 | | Back | 0x0020 |
| D-Pad Up | 0x0001 | | L3 | 0x0040 |
| D-Pad Down | 0x0002 | | R3 | 0x0080 |
| D-Pad Left | 0x0004 | | | |
| D-Pad Right | 0x0008 | | | |

### 使用规则编辑器

内置的**可视化规则编辑器**位于主界面右侧面板，支持：

- **规则列表**：展示当前配置的所有规则，支持拖拽排序（↑↓按钮）
- **前置条件**：以复选框形式选择需要的按键/LT/RT 前置条件
- **扳机行程滑块**：分别设置 LT/RT 的最小和最大行程阈值
- **摇杆幅度滑块**：设置左右摇杆的触发幅度下限
- **效果参数**：选择效果类型、模式、目标扳机、力度、速度、持续时间
- **添加/删除规则**：点击"新建规则"或"删除规则"管理规则列表
- **保存配置**：修改后点击"保存"写入 JSON 文件，引擎会自动加载

编辑器中每条规则支持**实时预览**——修改参数后无需重启引擎即可生效。

## 协议

基于 SpaceStation 私有 HID 协议逆向。ForceAdapt 协议通过 Report ID 0x03 + Magic 0x5AA5 下发 V2 命令序列。

## License

MIT
