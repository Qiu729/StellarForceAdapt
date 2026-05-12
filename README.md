# StellarForceAdapt

飞智 (FlyDigi) 手柄 ForceAdapt 自适应扳机通用适配器。

通过 USB HID 直连飞智手柄，下发 ForceAdapt 命令实现 6 种扳机力反馈模式。JSON 配置文件驱动，基于 XInput 输入实时响应。

## 功能

- **6 种力反馈模式**：Off / Racing / Machinegun / Sniper / TriggerLock / Vibration
- **XInput 输入捕获**：双扳机 + 按键 + 摇杆，~250Hz 轮询
- **JSON 配置驱动**：优先级排序、冷却时间、条件组合
- **左右扳机独立控制**：各自独立的模式和状态追踪
- **自动重连**：手柄断开后自动重试连接

## 支持手柄

| 型号 | PID |
|------|-----|
| 飞智八爪鱼5 (APEX 5) | 0x2501 |
| 飞智 Vader 4 Pro | 0x2012 |
| 飞智 APEX 4 | 0x2021 / 0x2023 |
| 飞智 Vader 3 Pro | 0x2011 |
| 飞智 Vader 3 | 0x2010 |

## 快速开始

1. 下载 [Releases](../../releases) 中的最新版本
2. 解压到任意目录
3. 通过 USB 连接飞智手柄
4. 运行 `StellarForceAdapt.exe`
5. 选择配置文件，点击"启动引擎"

## 配置文件

扳机效果规则位于 `Profiles/` 目录，JSON 格式：

```jsonc
{
  "name": "示例配置",
  "version": "1.0",
  "description": "RT按下时机枪反馈",
  "rules": [
    {
      "id": "rt_mg",
      "name": "RT 机枪反馈",
      "priority": 100,          // 优先级（数值越大越优先）
      "cooldown_ms": 0,         // 冷却时间（毫秒）
      "condition": {
        "buttons": 0,              // XInput 按键掩码（必须全部按下，0=不检查）
        "buttons_any": 0,          // 任意一个按下即触发（0=不检查）
        "left_trigger_min": 0,     // LT 下限 (0-255)
        "left_trigger_max": 255,   // LT 上限
        "right_trigger_min": 30,   // RT 下限
        "right_trigger_max": 255,  // RT 上限
        "left_stick_magnitude_min": 0,  // 左摇杆幅度下限 (0-32768)
        "right_stick_magnitude_min": 0  // 右摇杆幅度下限
      },
      "effect": {
        "type": "force_adapt",
        "mode": "machinegun",     // off / racing / machinegun / sniper / triggerlock / vibrate
        "target": "right",        // left / right / both
        "duration_ms": 0,         // 持续时间（0=持续）
        "intensity": 220,         // 力度 (0-255)
        "speed": 100              // 速度 (0-255)
      }
    }
  ]
}
```

### 条件字段说明

| 字段 | 类型 | 说明 |
|------|------|------|
| `buttons` | ushort | XInput 按键掩码，**全部按下**才触发。0=不检查 |
| `buttons_any` | ushort | XInput 按键掩码，**任意按下**即触发。0=不检查 |
| `left_trigger_min` | byte | 左扳机最小行程 (0-255) |
| `left_trigger_max` | byte | 左扳机最大行程 (0-255) |
| `right_trigger_min` | byte | 右扳机最小行程 (0-255) |
| `right_trigger_max` | byte | 右扳机最大行程 (0-255) |
| `left_stick_magnitude_min` | short | 左摇杆矢量幅度下限 (0-32768) |
| `right_stick_magnitude_min` | short | 右摇杆矢量幅度下限 (0-32768) |

### 效果类型

| type | 说明 |
|------|------|
| `force_adapt` | ForceAdapt 扳机力反馈（需要 mode 字段） |
| `rumble` | 扳机震动马达（需要 intensity 字段） |
| `sequence` | 时序效果序列（需要 sequence 数组） |

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

## 构建

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

要求：
- .NET 9.0 SDK
- Windows 10+ (XInput 依赖 `xinput1_4.dll`)

## 协议

基于 SpaceStation 私有 HID 协议逆向。ForceAdapt 协议通过 Report ID 0x03 + Magic 0x5AA5 下发 V2 命令序列。

## License

MIT
