# StellarForceAdapt

飞智八爪鱼5 (FlyDigi APEX5) ForceAdapt 扳机力反馈适配引擎。

通过逆向 SpaceStation 私有 HID 协议，实现游戏状态监测 → 动作识别 → 扳机力反馈的实时闭环控制。

## 功能

- 逆向飞智 5aa5 厂商 HID 协议，直接控制扳机马达
- 双报告 HID 通信（Report 0x03 + 0x04），左右扳机独立控制
- 支持阻力、振动等多种力反馈模式
- 游戏内存读取 + XInput 推断，毫秒级动作识别
- 配置文件驱动，无需重新编译即可调整触发策略

## 支持手柄

- 飞智八爪鱼5 (APEX 5, PID 0x2501)
- 飞智 Vader 4 Pro (PID 0x2012)
- 飞智 APEX 4 (PID 0x2021/0x2023)
- 飞智 Vader 3 Pro (PID 0x2011)

## 协议逆向

详见 `USBPcap捕获结果.txt`，包含 SpaceStation 官方软件的完整 USB 通信抓包。
