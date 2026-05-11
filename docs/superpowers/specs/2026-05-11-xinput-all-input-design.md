# XInput 全输入层迁移 — 双扳机支持

> 日期：2026-05-11
> 状态：设计已确认

## 问题

15字节 HID 报告使用单字节（buf[10]）对 LT/RT 做差值编码（>128=LT, <128=RT），两边扳机同时按下时差值相互抵消，无法独立读取各自位置。当前代码检测到此情况后直接将双方归零，导致双扳机输入完全丢失。

## 方案

**全 XInput 输入**：XInput 提供独立的 LT/RT 字节（bLeftTrigger / bRightTrigger），天然支持双扳机同时按下。摇杆同理（4个独立 short 轴）。将全部游戏手柄输入（按钮 + 扳机 + 摇杆）从 HID 迁移到 XInput。

HID 层保留仅用于 FlyDigiDevice 下发 ForceAdapt 命令。

## 架构

```
XInputWatcher ────→ 按钮 + LT+RT + 摇杆 ──→ StellarBladeMonitor.UpdateFromXInput
                                                      ↓
                                                 GameState
                                                      ↓
                                               MappingEngine
                                                      ↓
                                               FlyDigiDevice
```

## 改动清单

### 删除

| 文件 | 内容 | 原因 |
|------|------|------|
| `Monitor/HIDGamepadReader.cs` | 整个类 | 不再需要 HID 输入读取 |
| `Monitor/StellarBladeMonitor.cs` | `UpdateFromHID` 方法 | 不再需要 HID→GameState 转换 |
| `Mapping/MappingEngine.cs` | `_gamepad` 字段及 `OnGamepadStateChanged` | 移除 HID 输入依赖 |

### 修改

| 文件 | 改动 |
|------|------|
| `MappingEngine.cs` | `Start()` 移除 HID 分支，始终启动 XInput；移除 `_xinputActive` 字段 |
| `MainWindow.xaml.cs` | 移除 HIDGamepadReader 引用和相关 UI |

### 不变

| 文件 | 说明 |
|------|------|
| `HID/FlyDigiDevice.cs` | 命令下发，完全不涉及输入 |
| `HID/ForceAdaptProtocol.cs` | 协议定义 |
| `Mapping/TriggerProfile.cs` | 规则模型 |
| `Mapping/ControllerMapping.cs` | 保留类定义，按钮解码不再需要但 JSON 配置仍有用 |
| `Monitor/XInputWatcher.cs` | 无需改动，本身已支持所有数据 |
| `Monitor/StellarBladeMonitor.UpdateFromXInput` | 无需改动 |

## 影响范围

- 删除 ~220 行
- 修改 ~30 行
- 零新增依赖
- 不影响协议层和下发层
