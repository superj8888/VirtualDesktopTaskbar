# 虚拟桌面切换器（VirtualDesktopTaskbar）

一个极小的 Windows 11 虚拟桌面切换小组件：**伪嵌入任务栏**，显示在通知区域（隐藏图标 `^`）左侧，点击数字直接切换虚拟桌面，当前桌面高亮。

```
... [应用] [应用] [应用] │ 1 2 3 4 │ ^ │ Wi-Fi │ 音量 │ 电池
                         ↑
                    本程序（1 = 当前桌面，蓝色高亮）
```

- 不修改 / 不注入 Explorer，不真正嵌入任务栏——只是一个精确贴位的无边框 WPF 窗口
- 零第三方 NuGet 依赖
- 发布产物单文件约 200 KB

## 环境要求

- Windows 11 **24H2（build 26100）及以上**（25H1/25H2/26H1/26H2 向上兼容；不做旧系统兼容）
- .NET 10 Desktop Runtime（开发机已随 SDK 安装；纯运行机器需单独安装）

## 使用

```
dotnet publish VirtualDesktopTaskbar/VirtualDesktopTaskbar.csproj -c Release -o publish
publish\VirtualDesktopTaskbar.exe
```

- 启动后自动贴在 `^` 左侧，常驻所有虚拟桌面（PinView 固定）
- 托盘图标：右键菜单可开关**开机启动**、**退出**
- 按钮上滚轮/悬停有高亮；点击不抢当前窗口焦点

## 打包发布

### 正式发布（单文件，推荐）

```powershell
dotnet publish VirtualDesktopTaskbar/VirtualDesktopTaskbar.csproj -c Release -o publish
```

- 产物：`publish\VirtualDesktopTaskbar.exe`（约 200 KB，framework-dependent 单文件）
- csproj 已内置 `PublishSingleFile` + `RuntimeIdentifier=win-x64`，无需额外参数
- **目标机器需要 .NET 10 Desktop Runtime**（[下载地址](https://dotnet.microsoft.com/download/dotnet/10.0)，装 `Desktop Runtime` 即可）；本机已装 SDK 自带
- 把 `VirtualDesktopTaskbar.exe` 单独拷走也能用；托盘菜单可开机关启动，届时注意别移动 exe 位置（注册表 Run 键记录的是绝对路径）

### 免运行时版本（可选，体积大）

目标机器不想装运行时时，改用自包含发布（约 150 MB，用完可删 csproj 里的 `SelfContained` 改法）：

```powershell
dotnet publish VirtualDesktopTaskbar/VirtualDesktopTaskbar.csproj -c Release -r win-x64 --self-contained true -o publish-selfcontained
```

### 本地调试构建

```powershell
dotnet build VirtualDesktopTaskbar/VirtualDesktopTaskbar.csproj
# 输出：VirtualDesktopTaskbar\bin\Debug\net10.0-windows\win-x64\VirtualDesktopTaskbar.exe
```

### COM 探针（开发用，可选）

与主程序共享同一份 interop 源码，用于验证 COM ABI 与定位几何：

```powershell
dotnet run --project tools/Probe/Probe.csproj --            # 只读自检
dotnet run --project tools/Probe/Probe.csproj -- --switch   # 真实切换测试（切过去再切回来）
dotnet run --project tools/Probe/Probe.csproj -- --pin      # 窗口固定测试
dotnet run --project tools/Probe/Probe.csproj -- --fullscreen # 全屏隐藏自测
dotnet run --project tools/Probe/Probe.csproj -- --where    # 任务栏几何 / 组件窗口状态
```


## 工作原理

```
VirtualDesktopManager        TaskbarManager              MainWindow
(未公开 COM：切换/枚举)       (Shell_TrayWnd→TrayNotifyWnd) (无边框窗口 + 按钮)
        │                        │                          │
        └────────── 250ms 轮询 ───┴────── TaskbarWatcher ───┘
                          （同步高亮 / 重定位 / 自愈）
```

### 虚拟桌面（`VirtualDesktop/`）

- **主路径**：未公开 COM。`CoCreateInstance(ImmersiveShell)` → `QueryService(CLSID_VirtualDesktopManagerInternal)` → `IVirtualDesktopManagerInternal`，vtable 槽位调用：
  - `GetDesktopCount`（槽 0）、`GetCurrentDesktop`（槽 3）、`GetDesktops`（槽 4）、`SwitchDesktop`（槽 6）
  - `IObjectArray.GetAt`（注意：元素只能用 `IID_IVirtualDesktop` 索引，传 `IID_IUnknown` 会得到 `E_NOINTERFACE`）
  - 当前桌面匹配用 `IVirtualDesktop::GetID` 的 GUID（内部对象 QI 表不含 IUnknown，不能用身份比较）
  - 窗口跨桌面常驻：`IApplicationViewCollection.GetViewForHwnd` + `IVirtualDesktopPinnedApps.PinView`
- **自检**：启动只读自检（数量 → 枚举 → 当前桌面 GUID 匹配）全部通过才进 Native 模式
- **兜底**：自检失败或运行中断连 → 按 `Win+Ctrl+←/→` 相对切换（官方没有“数字直达桌面”快捷键，`Win+Ctrl+数字` 是切换任务栏固定应用）；索引未知时先回桌面 1 再右移到目标（覆盖 ≤10 个桌面），已知索引单次相对移动上限 16 步；降级期间每 2 秒自动重试原生连接
- GUID 来源（交叉验证）：Ciantic/VirtualDesktopAccessor（MIT，实测 26100.2605 / 26200）、mntone/VirtualDesktop、Grabacr07/VirtualDesktop

### 任务栏定位（`Taskbar/`）

- `Shell_TrayWnd` → `TrayNotifyWnd`（通知区域整体容器，其左边缘即 `^` 左边缘，`^` 不存在时即快设组左边缘）
- `窗口X = TrayNotifyWnd.Left - 间距 - 宽度`，Y/高与任务栏对齐，宽度随桌面数与 DPI 缩放
- 不使用 UI Automation

### 稳定性

- **250ms 巡检**：位置跟随 + 当前桌面高亮同步 + COM 断连自愈
- **z 序保持**：任务栏与组件同在 topmost 带内，任务栏重排会盖住组件，巡检周期把组件压回 `HWND_TOPMOST` 顶端
- **Explorer 重启**：`TaskbarCreated` 广播 + 轮询重试双保险——重新定位、重建 COM、重加托盘图标、重新固定窗口
- **全屏**：前台窗口完整覆盖显示器时自动隐藏（最大化不算全屏），退出恢复
- **不抢焦点**：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` + `WM_MOUSEACTIVATE→MA_NOACTIVATE`
- **窗口显隐**：WPF `Visibility` + `SetWindowPos(SWP_SHOWWINDOW/HIDEWINDOW)` 双保险
- 多显示器：跟随主任务栏（有托盘的那块）；DPI 感知（PerMonitorV2）

## 已知限制

- 未公开 COM 接口未来若被微软改动，程序自动降级为键盘模拟模式并弹气泡提示（不会崩溃），届时需按新 build 更新 `VirtualDesktopInterop.cs` 的槽位/IID
- 降级模式下无法感知键盘切换，高亮为最近一次点击的乐观值
- 自动隐藏任务栏（auto-hide）场景未做跟随隐藏
- `tools/Probe` 是开发用 COM 探针（`--switch` / `--pin` / `--fullscreen` / `--where`），与主程序共享同一份 interop 源码，保证"测到的就是用到的"

## 目录结构

```
VirtualDesktopTaskbar/
├── VirtualDesktop/
│   ├── VirtualDesktopInterop.cs    # GUID / vtable 槽位 / 委托（唯一定义处）
│   ├── VirtualDesktopManager.cs    # 自检 / 轮询 / 切换 / 固定 / 降级
│   └── VirtualDesktopFallback.cs   # Win+Ctrl+←/→ 相对切换兜底
├── Taskbar/
│   ├── NotificationAreaFinder.cs   # Shell_TrayWnd → TrayNotifyWnd
│   ├── TaskbarManager.cs           # 锚点 → 组件矩形
│   └── TaskbarWatcher.cs           # 250ms 巡检 + 系统消息响应
├── FullscreenWatcher.cs
├── MainWindow.xaml(.cs)            # 无边框窗口 / 按钮 / 托盘 / 可见性状态机
├── NativeMethods.cs                # Win32 P/Invoke
├── Settings.cs                     # 开机启动 + 日志
└── app.manifest                    # PerMonitorV2 DPI
tools/Probe/                        # COM 探针（开发用）
```
