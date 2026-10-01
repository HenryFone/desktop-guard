# DesktopGuard（桌面布局守卫）

一个 Windows 桌面小工具：**在外部看住「桌面收纳盒 / 桌面小组件」的位置数据**，屏幕布局一变（插拔显示器、改分辨率、改缩放）它自动把跑出屏幕的坐标修回来，并且每次变化都做一份异地备份。

> 适用对象：360桌面助手（360DesktopLite）。思路是通用的，换成别的桌面收纳软件只需要改一个适配器文件。

## 它解决什么问题

360桌面助手把每个收纳盒的**绝对像素坐标**写进注册表和 `DTFence` 数据文件。

平时没问题；但一旦发生下面任何一种情况：

- 笔记本插上/拔掉外接显示器
- 改分辨率或缩放比例
- 显示器换了摆放位置
- explorer.exe 崩溃重启

坐标就会指向一块不存在的屏幕区域。后果是：**桌面收纳盒"消失"了**，你只能手动找回来。而且它自己不会修复，也不会提醒你——你发现时通常已经是"我的桌面怎么乱成这样"。

DesktopGuard 的思路是：**不改它的程序文件**（改了会破坏签名，而且它自更新会覆盖），而是在外面加一层守护，抢在数据被写坏之前修好。

## 三个职责

| 职责 | 做什么 |
| --- | --- |
| 看住 explorer | explorer 崩溃重启会导致桌面叠加层失效，组件负责侦测并等它恢复 |
| 看住屏幕布局 | 插拔屏 / 改分辨率 / 改缩放时重新计算每个收纳盒该在哪，并修正越界坐标 |
| 看住布局数据 | 每次布局变化都存一份带时间戳的快照，支持回溯和异地备份 |

## 核心设计

- **布局指纹**：把显示器组合（设备名 + 分辨率 + 缩放 + 坐标 + 主屏标记）算成一个指纹。指纹没变就不做任何事，不打扰系统。
- **双坐标存储**：同时保存绝对像素和归一化比例。同一个布局可以直接还原像素坐标；换了布局则按比例换算。
- **原子写入 + 自动回退**：写配置用「临时文件 → 旧文件转 .bak → 新文件顶上」。主文件坏了自动读 .bak，并保留损坏文件供排查。
- **只读判断 + 最小写入**：正常情况只读不写；只有确认越界才写回坐标，写之前先把原值记进日志。

## 怎么用

### 先看当前状态（不改任何东西）

```powershell
dotnet run -- --status
```

它会打印：有几台显示器、每台的分辨率和缩放、当前保存的布局指纹、目标应用记录的坐标、有没有越界。

### 单次巡检（跑一次就退出）

```powershell
dotnet run -- --once
```

适合放进计划任务，比如每 5 分钟跑一次。

### 常驻托盘

```powershell
dotnet run
```

常驻监听布局变化和 explorer 事件，右下角托盘图标可退出。

### 每日异地备份（可选）

```powershell
.\backup-360-layout.ps1
```

把目标应用的布局数据 + 注册表坐标打包到 `%LOCALAPPDATA%\DesktopGuard\external-backup`，只保留最近 30 份。
想改备份位置，先设环境变量：

```powershell
$env:DESKTOP_GUARD_BACKUP_DIR = "E:\my-backups"
.\backup-360-layout.ps1
```

## 编译与验证状态

需要 .NET 8 SDK（更高版本 SDK 也能编译，只要装了 net8.0 目标包）。

已验证：Windows 11 22631 + .NET SDK 10.0.301 下 `dotnet build -c Release` 通过，0 警告 0 错误；生成的 `DesktopGuard.exe --once` 实际跑过一次，正确识别出越界坐标并修复（修改前的原值会写进 `guard.log`）。

小坑：PATH 里若同时存在 32 位 dotnet，`dotnet --list-sdks` 可能显示「没有 SDK」——那只是缺 SDK 的 x86 版本，改用 `C:\Program Files\dotnet\dotnet.exe` 即可。

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false
```

## 目录与配置

| 位置 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%\DesktopGuard\layout.json` | 最近一次布局快照 |
| `%LOCALAPPDATA%\DesktopGuard\layout.json.bak` | 上一次快照（自动回退用） |
| `%LOCALAPPDATA%\DesktopGuard\history\` | 按指纹归档的历史快照 |
| `%LOCALAPPDATA%\DesktopGuard\guard.log` | 运行日志 |

环境变量：

| 变量 | 作用 |
| --- | --- |
| `DESKTOP_GUARD_DATA_ROOT` | 覆盖目标应用的数据目录（默认 `%APPDATA%\360DesktopLite`） |
| `DESKTOP_GUARD_BACKUP_DIR` | 覆盖外部备份目录 |

## 已知边界（如实说明）

- 这是**外部修复**，不是官方补丁。目标应用逻辑大改、注册表键名变了，需要同步改 `src/DesktopLiteAdapter.cs`。
- 修改坐标前会检查目标进程是否在运行；不在运行时写入可能被它启动时覆盖。
- 托盘守护只有在进程活着的时候才生效；要 7×24 生效请配合计划任务跑 `--once`。
- 目前只在 Windows 11（22631）+ .NET 8 上实测过。

## License

MIT