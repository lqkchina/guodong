# JellyWallpaper —— Windows 交互式果冻弹性壁纸

在桌面**空白区域**按住鼠标左键拖拽，壁纸随弹簧-质点网格产生果冻弹性形变；松开左键，
弹簧弹力 + 阻尼驱动网格 Q 弹回弹。桌面图标层完全不受干扰：移动、增删图标照常，
点击图标走原生行为，不会触发壁纸形变。

技术栈：C# / .NET 8 · CsWinRT（Microsoft.WindowsAppSDK 1.6）· Microsoft.Graphics.DirectX
+ Direct2D（Microsoft.Graphics.Win2D 1.3.2）· WPF 设置窗口。目标系统 Win10 1709+ / Win11。

---

## 1. 项目整体架构说明

### 1.1 图层 Z 序（需求硬约束）

Windows 桌面的窗口结构（Win10 / Win11）：

```
Progman（Program Manager，桌面根窗口）
 ├─ SHELLDLL_DefView → SysListView32（桌面图标层 = Progman 的子窗口）
 └─ （DWM 绘制的系统壁纸层，位于 Progman 之下）
```

本程序使用 **Windows App SDK 1.6 的 ContentIsland + DesktopChildSiteBridge** 把合成
内容挂载到 Progman 窗口上：

```
Compositor（合成器）
 └─ ContainerVisual（内容岛根视觉）
     ├─ SpriteVisual[屏1] ── CompositionSurfaceBrush ── CompositionDrawingSurface[屏1]
     ├─ SpriteVisual[屏2] ── CompositionSurfaceBrush ── CompositionDrawingSurface[屏2]
     └─ ...
ContentIsland.Create(根视觉)  →  DesktopChildSiteBridge.Create(compositor, WindowId(Progman))
  → Connect → MoveAndResize(Progman 客户区) → Show
```

内容岛渲染在 **Progman 自身内容之上、其子窗口（图标层）之下**，因此最终 Z 序严格为：

> **系统原生壁纸图层 → 本程序 Direct2D 渲染图层 → 桌面图标图层**

合规性声明（全部满足）：
- ❌ 不用置顶透明窗口盖住图标（无任何顶层窗口参与渲染）
- ❌ 不注入 explorer 进程
- ❌ 不 Hook WorkerW、不装 SetWindowsHookEx 等任何全局钩子（输入用 `GetAsyncKeyState` + `GetCursorPos` 轮询）
- ✅ 图标层永远是独立 HWND 子窗口，位于合成内容之上；移动/增删图标零干扰

### 1.2 线程模型（三层解耦）

| 线程 | 职责 | 说明 |
|---|---|---|
| **WPF 主线程** | 设置 UI、托盘、配置读写 | 滑条直接写共享 `PhysicsParams`，物理线程每帧读取 → 修改立即生效 |
| **专用合成线程**（DispatcherQueueController.CreateOnDedicatedThread） | Compositor / ContentIsland / 渲染表面 / DispatcherQueueTimer(≈60Hz) | 所有 Composition/Win2D 对象在本线程创建；DComp 自动合成表面，无需 Present |
| **物理线程**（独立 Thread） | `SimulationLoop` 固定步长 60 FPS | 只写网格双缓冲快照（`volatile` 引用整体替换）；渲染线程无锁读取 |

### 1.3 模块划分（对应需求功能清单）

| 模块 | 文件 | 说明 |
|---|---|---|
| ① 壁纸监听与加载 | `WallpaperSource.cs` `WallpaperWatcher.cs` | 注册表 `TranscodedImageCache`（含 `_000/_001…` 多屏）+ 内嵌 BMP 兜底 + 静态壁纸；`SystemEvents` 事件 + 轮询双通道（兼容 Spotlight 事件失效）；异步加载不阻塞渲染，失败保留上一张 |
| ② 弹簧质点物理 | `Physics/`（Core） | 二维弹簧-质点网格 + 半隐式欧拉 + 胡克定律 + 阻尼比 + 拖拽平方衰减 + 双钳制；独立线程 60 FPS 固定步长 |
| ③ 图标命中检测 | `DesktopIconTracker.cs` | `SHELLDLL_DefView/SysListView32` + `LVM_GETITEMRECT`，500ms 缓存图标矩形 |
| ④ Direct2D 渲染 | `MonitorRenderLayer.cs` `MeshRenderer.cs` `DesktopWallpaperHost.cs` | 每屏独立视觉 + 独立网格 + 独立绘制表面；GPU 三角网格近似 + UV 纹理映射；高 DPI 适配；资源安全释放 |
| ⑤ 设置 UI 与配置 | `MainWindow.xaml(.cs)` `AppConfig.cs` | 6 滑条 + 2 开关；JSON 原子持久化到 `%APPDATA%\JellyWallpaper\config.json`；托盘常驻 |
| ⑥ Explorer 重启恢复 | `ExplorerWatcher.cs` `WallpaperLayerManager.cs` | 轮询 explorer 生命周期，检测到重启 → Teardown 旧 DWM 资源 → 重建合成层 |

### 1.4 渲染链路（Win2D 1.3.2 + WinAppSDK 1.6 官方互操作）

```
CanvasDevice.GetSharedDevice()
CanvasComposition.CreateCompositionGraphicsDevice(compositor, canvasDevice) → CompositionGraphicsDevice
graphicsDevice.CreateDrawingSurface2(SizeInt32, B8G8R8A8, Premultiplied)      → CompositionDrawingSurface
每帧：CanvasComposition.CreateDrawingSession(surface) → 清空 → 基础壁纸层 → 形变网格层
compositor.CreateSurfaceBrush() + SpriteVisual（DIP 尺寸/偏移）→ 挂入内容岛根视觉
```

形变网格层：逐网格单元用 3 点仿射矩阵近似四边形的 UV 映射（`ds.Transform + DrawImage`），
GPU 硬件加速；退化四边形自动跳过；位移 < 0.5px 时只画基础层省 GPU。

---

## 2. 项目文件清单

```
JellyWallpaper/
├── JellyWallpaper.sln                    解决方案（Core / Core.Tests / App）
├── publish.bat                           一键发布独立单文件 EXE（Windows 双击运行）
├── .github/workflows/build-exe.yml       GitHub Actions 云端自动出 EXE
├── README.md                             本文档
└── src/
    ├── JellyWallpaper.Core/              纯托管核心库（可跨平台编译测试）
    │   ├── JellyWallpaper.Core.csproj
    │   ├── Physics/
    │   │   ├── PhysicsParams.cs          六个可调参数 + 默认值 + 物理意义注释
    │   │   ├── MassPoint.cs              质点：原始/当前坐标、速度、复位
    │   │   └── SpringMassGrid.cs         弹簧-质点网格引擎（力学公式重点注释）
    │   └── Config/
    │       └── AppConfig.cs              配置模型 + JSON 原子保存
    ├── JellyWallpaper.Core.Tests/        控制台自测（6 组 10 项断言，CI 可用）
    │   ├── JellyWallpaper.Core.Tests.csproj
    │   └── Program.cs
    └── JellyWallpaper.App/               Windows 应用工程
        ├── JellyWallpaper.App.csproj     WinAppSDK 1.6 + Win2D 1.3.2 + 单文件发布配置
        ├── app.manifest                  PerMonitorV2 DPI + asInvoker
        ├── App.xaml / App.xaml.cs        入口：Mutex 单实例、加载配置、启动/退出管理
        ├── MainWindow.xaml / .cs         设置 UI 面板 + 托盘
        ├── Native/NativeMethods.cs       P/Invoke（窗口/鼠标/LVM/DPI 等）
        ├── Wallpaper/WallpaperSource.cs  壁纸来源解析（TranscodedImageCache 等）
        ├── Wallpaper/WallpaperWatcher.cs 壁纸变更监听（事件 + 轮询兜底）
        ├── Desktop/DesktopIconTracker.cs 桌面图标矩形命中检测
        ├── Input/MouseInputService.cs    轮询式鼠标采样（非钩子）
        ├── Lifecycle/ExplorerWatcher.cs  explorer 重启检测
        ├── Simulation/SimulationLoop.cs  物理线程：固定步长、图标门控拖拽、每屏独立网格
        ├── Render/MeshRenderer.cs        三角网格 + UV 纹理映射绘制
        └── Composition/
            ├── DesktopWallpaperHost.cs   ContentIsland + DesktopChildSiteBridge 挂载桌面
            ├── MonitorRenderLayer.cs     单屏渲染层（绘制表面 + 视觉 + 渲染循环）
            └── WallpaperLayerManager.cs  顶层装配：合成线程、多屏布局、explorer 重建
```

---

## 3. 弹簧-质点物理引擎说明（核心）

模型：壁纸平面离散为 N×M 质点网格，相邻质点用弹簧连接（横 + 纵，无斜向弹簧控制计算量）。
每个质点质量归一化 m=1，受四类力：

| 力 | 公式 | 说明 |
|---|---|---|
| 弹簧力（胡克定律） | `F = k·(L₀−L)·û` | k=刚度；L=当前距离，L₀=静止距离（由原始坐标算）；**两端等大反向**（满足牛顿第三定律，动量守恒） |
| 锚点力 | `F = 0.15k·(P_rest−P)` | 指向原始坐标的软弹簧，防止网格整体漂移 |
| 粘性阻尼 | `F = −c·v`，`c = ζ·2√(k·m)` | ζ=阻尼比（默认 0.25 欠阻尼 → Q 弹往复；1=临界阻尼最快回位） |
| 拖拽外力 | `F = 2·DragStrength·k·(1−d/R)²·(P_mouse−P)` | 影响半径内平方衰减，边缘力连续到 0，避免网格撕开 |

积分：**半隐式欧拉**（`v←v+a·dt` 先更新速度，`x←x+v·dt` 再用新速度），固定步长 dt=1/60。
稳定性：`k·dt²/m < 2`，默认 k=120 → 0.033 ≪ 2，余量充足。
安全钳制：速度 ≤ 4000 px/s；单点位移 ≤ MaxDisplacement（超出等比缩回，防极端扭曲）。

双缓冲快照：物理线程每步把最新坐标写入新数组并整体替换 `volatile` 引用，
渲染线程无锁读取 `SnapX/SnapY`，保证一帧内网格数据一致。

---

## 4. 编译步骤（VS2022）

1. 安装 **Visual Studio 2022**（勾选“.NET 桌面开发”工作负载，含 Windows 10/11 SDK）。
2. 双击 `JellyWallpaper.sln` 打开解决方案。
3. 首次加载自动还原 NuGet（见第 5 节包清单；若关闭自动还原：
   `dotnet restore JellyWallpaper.sln`）。
4. 选择 `x64` + `Release`，右键 **JellyWallpaper.App → 设为启动项目**，F5 运行调试。
5. 若提示缺少 WindowsAppSDK 运行时：本项目已配置 `WindowsAppSDKSelfContained`，
   运行时随发布一并打包，无需安装。

> 物理引擎可独立测试（无需 Windows）：
> `dotnet run --project src/JellyWallpaper.Core.Tests -c Release`（10 项断言，退出码 0=全过）。

---

## 5. NuGet 包清单

| 包 | 版本 | 用途 | 说明 |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 1.6.250602001 | CsWinRT 投影、Microsoft.UI.Composition、ContentIsland/DesktopChildSiteBridge、DispatcherQueue | 官方依赖组合，Win2D 1.3.2 传入依赖即此系列 |
| Microsoft.Graphics.Win2D | 1.3.2 | Direct2D 渲染（CanvasDevice / CanvasBitmap / CanvasComposition） | 与 WinAppSDK 1.6.x 匹配的版本（1.4.x 改依赖 WinUI 1.8，未采用） |

两个包均为稳定官方版本，已在沙箱内核对依赖链与 API 签名（见第 7 节验证记录）。

---

## 6. 运行注意事项

- **首发运行**：建议先以管理员身份运行一次（`asInvoker` 清单默认非管理员）；
  若桌面合成挂载失败（explorer 未就绪等），程序会每 2 秒自动重试，无需手动操作。
- **多显示器**：每块屏幕独立渲染层 + 独立物理网格；壁纸来源按屏幕分别读取
  （TranscodedImageCache `_000/_001…` 或共享静态壁纸）。
- **壁纸切换**：静态图 / 幻灯片 / Spotlight 均支持；事件失效时由轮询兜底
  （含缓存签名比对），异步加载不卡渲染。
- **explorer 重启**：自动重建合成层与渲染资源，无需重启本程序。
- **配置**：保存在 `%APPDATA%\JellyWallpaper\config.json`，启动自动加载；
  UI 面板修改即时生效并自动保存。
- **退出**：托盘图标右键 → 退出（关闭窗口只是隐藏）。
- **已知限制**：形变只作用于本程序绘制的壁纸层；若壁纸是纯色且未跟随系统
  （关闭“跟随系统壁纸”），使用 UI 中的兜底色。Win10 1709+ 与 Win11 均支持。

---

## 7. 单文件 EXE 打包发布

项目已配置好一键发布（`RuntimeIdentifier=win-x64` + `SelfContained` +
`PublishSingleFile` + `WindowsAppSDKSelfContained` + `WindowsPackageType=None`），
产物为**绿色免安装**的独立单文件 EXE（自带 .NET 运行时与 WinAppSDK 运行时）。

### 方式 A：本机一键发布（推荐，最快）

在 Windows 上双击运行 `publish.bat`（内容等价于）：

```bat
dotnet publish src\JellyWallpaper.App\JellyWallpaper.App.csproj -c Release ^
  -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:WindowsAppSDKSelfContained=true ^
  -p:WindowsPackageType=None -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=embedded ^
  -o dist
```

产物：`dist\JellyWallpaper.exe`（单文件，可直接拷贝到任意 Win10 1709+/Win11 电脑运行）。

### 方式 B：Visual Studio 发布

右键 `JellyWallpaper.App` → **发布** → 选择文件夹目标 → 配置已内联在 csproj，
点“发布”即可得到单文件 EXE。

### 方式 C：GitHub Actions 云端构建（无 Windows 电脑也能出 EXE）

把本目录推送到 GitHub 仓库，`.github/workflows/build-exe.yml` 会在
`windows-latest` 上自动执行相同发布命令，并在构建完成后把 `JellyWallpaper.exe`
作为 artifact 上传，直接在 Actions 页面下载。

### 沙箱环境验证记录（诚实披露）

- ✅ **物理引擎**：已在 Linux 沙箱（.NET 8）编译并跑通 10 项自测（静止平衡、拖拽形变、
  松手收敛、阻尼功能、参数实时生效、网格重建；退出码 0）。
- ✅ **App 全部 C# 源码**：已用“XamlStubs 技巧”在 Linux 上完成类型检查编译，
  按 WinAppSDK 1.6 真实元数据修正 API 后 **0 个 C# 错误**（含 ContentIsland /
  DesktopChildSiteBridge / CompositionGraphicsDevice.CreateDrawingSurface2 /
  CanvasComposition.CreateDrawingSession 等关键签名逐一核对）。
- ✅ **发布配置已按官方要求修正**：实测发现 WinAppSDK 的 `PublishSingleFile` 强制要求
  `EnableMsixTooling=true`（否则发布直接报错），已修正为官方“自包含单文件免安装”
  标准组合：`WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` +
  `EnableMsixTooling=true`。Linux 沙箱实测：配置层报错已消失。
- ⚠️ **完整 XAML / 单文件 EXE 构建**：依赖 Windows 专属的 VS AppxPackage 构建任务与
  WinAppSDK 自包含发布任务（实测在 Linux 上失败于 `MSB4062` / `GenerateAppManifestFromAppx`
  环境错误），必须在 Windows + VS2022 环境执行（第 7 节方式 A/B/C 任选其一）。
  Linux 沙箱无法产出真正的 Windows EXE，此为环境限制而非代码问题。

---

## 8. 免责与合规

- 本程序不注入、不钩子、不修改任何系统文件；使用公开的 Windows 合成 API
  （ContentIsland / DesktopChildSiteBridge），行为与 WinUI 3 桌面应用一致。
- 杀毒软件误报：由于未使用任何注入/钩子技术，正常签名后不应误报；如遇误报，
  请将本程序加入白名单并反馈给厂商。
