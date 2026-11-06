# LanIPcheck（局域网 IP 占用图）v2

Windows 单文件桌面工具：用格子热力图查看局域网哪些 IP 已被占用，并检测 IP 冲突。

## 功能

- **红 / 绿 / 黄 / 紫**：占用、空闲、曾占用、**IP 冲突**
- **IP 冲突检测**：定时扫描时，若同一 IP 先后出现不同 MAC 或设备名，格子变为紫色并记录所有曾见身份
- 多方式探测：ARP、ICMP、TCP（不只 Ping）
- 悬停显示设备名称、制造商、MAC；冲突格显示曾见 MAC/名称列表
- **导出表格**：含「曾见MAC / 曾见名称」列
- 定时扫描、开机启动、系统托盘
- 兼容 Windows 7 SP1 ~ Windows 11

## 下载

到 [Releases](https://github.com/fig3458/LanIPcheck/releases) 下载 LanIpWatcher.exe。

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

## 冲突说明

开启「定时」后，工具会跨次扫描累计每个 IP 的 MAC 与主机名：

- 同一 IP 出现 **≥2 个不同 MAC**，或 **≥2 个不同设备名** → 判定为冲突（紫格）
- 冲突记录会保存，重启后仍显示；可在「设置」里点「清除冲突」

## 说明

厂商 OUI 数据来源于 Wireshark manuf。

本目录为相对原版 `LanIpWatcher` 的新版本（含冲突检测）。

## 许可证

[MIT](LICENSE)
