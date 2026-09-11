# LanIPcheck（局域网 IP 占用图）

Windows 单文件桌面工具：用格子热力图查看局域网哪些 IP 已被占用。

## 功能

- **红 / 绿 / 黄**：占用、空闲、曾占用
- 多方式探测：ARP、ICMP、TCP（不只 Ping）
- 悬停显示设备名称、制造商、MAC
- 定时扫描、开机启动、系统托盘
- 兼容 Windows 7 SP1 ~ Windows 11

## 运行

直接运行发布产物 `LanIpWatcher.exe`（厂商库与图标已内嵌）。

- Win10 / Win11：通常免装运行库
- Win7 SP1：需 [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48)（或至少 4.0）

## 从源码编译

```bat
build.bat
```

产出：`dist\portable\LanIpWatcher.exe`

依赖：系统自带的 .NET Framework 4.x 与 `csc.exe`（无需 Visual Studio）。

## 说明

厂商 OUI 数据来源于 Wireshark manuf。
