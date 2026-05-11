# StellarForceAdapt

飞智八爪鱼5 (FlyDigi APEX5) ForceAdapt 扳机力反馈适配引擎。

通过逆向 SpaceStation 私有 HID 协议，实现 XInput 输入捕获 → 游戏状态监测 → 动作识别 → 扳机力反馈的实时闭环控制。

## 功能

- **ForceAdapt V2 协议**：左右扳机独立控制，6 种力反馈模式（Off / Racing / Machinegun / Sniper / TriggerLock / Vibration）
- **XInput 全输入捕获**：双扳机位置 + 按键 + 摇杆，~200Hz 轮询，支持 Xbox / FlyDigi 等 XInput 兼容手柄
- **Cheat Engine 桥接**：通过 CE Lua 脚本读取游戏内存（HP、Beta、Burst、Tachy 能量值），写入二进制文件由 C# 端实时轮询
- **JSON 配置驱动**：触发规则优先级排序、冷却时间、条件组合，无需重新编译即可调整策略
- **游戏进程自动检测**：后台监控目标进程启动/退出，自动启停引擎

## 支持手柄

- 飞智八爪鱼5 (APEX 5, PID 0x2501)
- 飞智 Vader 4 Pro (PID 0x2012)
- 飞智 APEX 4 (PID 0x2021/0x2023)
- 飞智 Vader 3 Pro (PID 0x2011)

## Cheat Engine 桥接（Stellar Blade / 剑星）

项目包含 Cheat Engine 桥接，可将游戏内存数据（生命值、能量槽）实时传入自适应扳机引擎。

### 环境要求

- **Cheat Engine 7.5+**：[https://www.cheatengine.org/](https://www.cheatengine.org/)
- **NidasBot 的 Stellar Blade CT 表**（需开启 `[Player Pointers]`）
- 游戏进程：`SB-Win64-Shipping.exe`（无 Anti-Cheat）

### 使用步骤

1. 启动游戏，进入实际操控角色（指针链仅在玩家角色加载后解析）
2. 打开 Cheat Engine，附加 `SB-Win64-Shipping.exe`
3. 加载 NidasBot 的 CT 表，勾选 **[Player Pointers]** 使其激活
4. `Ctrl+Alt+L` 打开 CE Lua 脚本窗口，粘贴 `tools/ce_pipe_server.lua` → 执行
5. 启动 `StellarForceAdapt.exe`，引擎将自动检测 CE 数据并生效

CE 状态数据写入 `%ProgramData%\StellarForceAdapt\ce_state.bin`，C# 端以 5ms 间隔轮询，基于递增时间戳判断数据新鲜度。

## 构建

```bash
dotnet build src/StellarForceAdapt/StellarForceAdapt.csproj
```

目标框架：.NET 9.0 Windows (WPF)，依赖 `Microsoft.XInput.winmd`（Windows SDK）。

## 配置文件

扳机效果规则位于 `src/StellarForceAdapt/Profiles/`，JSON 格式：

```jsonc
{
  "id": "low_health_lt",           // 唯一规则 ID
  "name": "低血量 - LT阻尼",
  "priority": 180,                  // 优先级（数值越大越优先）
  "cooldown_ms": 2000,             // 冷却时间
  "condition": {
    "action": "any",               // 触发动作（melee_attack / aiming / shooting / blocking …）
    "in_combat": true,             // 战斗状态过滤
    "health_percent_max": 0.3      // CE 数据条件：HP < 30%
  },
  "effect": {
    "type": "force_adapt",         // force_adapt / rumble / sequence
    "mode": "racing",              // 力反馈模式（off / racing / machinegun / sniper / triggerlock / vibrate）
    "position": 30,
    "intensity": 220,
    "speed": 100,
    "target": "left",             // left / right / both
    "duration_ms": 3000
  }
}
```

## 协议逆向

详见 `USBPcap捕获结果.txt`，包含 SpaceStation 官方软件的完整 USB 通信抓包。

ForceAdapt 协议格式（Report ID 0x03）：

| Offset | Size | 内容 |
|--------|------|------|
| 0x00 | 1 | Report ID (0x03) |
| 0x01 | 2 | Magic (0x5AA5, little-endian) |
| 0x03 | 1 | 触发侧 (1=LT, 2=RT, 3=Both) |
| 0x04 | 1 | 模式 (0=Off, 1=Racing, 2=Machinegun, 3=Sniper, 4=TriggerLock, 5=Vibration) |
| 0x05 | 4 | 参数 (position/intensity/speed 等，模式相关) |
