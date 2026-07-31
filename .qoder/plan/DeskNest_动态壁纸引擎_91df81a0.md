# DeskNest 动态壁纸引擎

## 摘要
在桌面图标层之下渲染动态壁纸，架构上比 Wallpaper Engine 更省电、更现代：
- **挂载**：Progman `0x052C` 催生 WorkerW，`SetParent` 每显示器一个渲染宿主到其下；监听 `TaskbarCreated` 自愈。
- **渲染**：视频/图片走 `MediaElement`(MediaFoundation 硬解)，网页/着色器/场景走 `WebView2`(WebGPU 优先、回退 WebGL2)。
- **先进能力（全部纳入）**：遮挡+电源自适应帧率调度、WASAPI 环回 FFT 音频总线、WebGPU 运行时、交互式壁纸、自制场景编辑器。
- **复用**：WebView2 宿主链路（`MarkdownNoteWindow`/`EditorBridge`/`WriteHost` 嵌入资源自解压）、`AudioCapture`(NAudio 环回) + `NAudio.Dsp` FFT、`WindowRegionHelper`、多屏换算、`SettingsWindow` 13 页范式、`BeeXPaths`、NaN-safe `AppState`。依赖无需新增（WebView2 1.0.2792.45 / NAudio 2.2.1 已在）。

## 三阶段
- **阶段一**：挂载+每屏宿主+视频/图片+遮挡/电源调度器+设置页/路径/托盘/图库骨架+退出清理与重挂载。端到端可用且比 WE 省电。
- **阶段二**：WebView2 网页/着色器运行时(WebGPU/WebGL2)+音频 FFT 总线+网页壁纸导入(可选 WE-HTML 兼容 shim)+交互式指针。
- **阶段三**：自制场景/粒子编辑器(图层/属性/音频绑定)+图库完善。

---

## 新增文件（均放 `src\Wallpaper\`）
- `WallpaperService.cs`：编排器。由 `DeskNestService` 持有（仿 `FileIndex`）；管理每屏宿主生命周期、显示器增删（`SystemEvents.DisplaySettingsChanged`）、锁屏暂停（`SessionSwitch`）、驱动调度器与音频总线；对外 `Start/Stop/ShowGallery/ApplyPreferences`。
- `DesktopWallpaperHost.cs`：WorkerW 挂载全部 P/Invoke（当前仓库没有，这是唯一新写的底层）。
- `WallpaperWindow.xaml(.cs)`：每显示器渲染宿主窗（无边框、`AllowsTransparency`、`WS_EX_NOACTIVATE|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW`），内容按类型挂 `MediaElement`/`Image`/`WebView2`。
- `AudioSpectrumBus.cs`：单路 `WasapiLoopbackCapture` + `NAudio.Dsp.FastFourierTransform` → 对数频带+节拍，事件广播（阶段二）。
- `VisibilityGovernor.cs`：遮挡矩形并集/差集 + 电源状态 → 每屏目标 FPS（纯算法，可单测）。
- `WallpaperRuntimeBridge.cs`：Web 壁纸 C#↔JS 桥（`EditorBridge` 同款 `PostWebMessageAsJson`/`WebMessageReceived`），下发 time/audio/pointer/monitor/pause，SDK `window.BeeXWallpaper`（阶段二）。
- `Models`（并入 `Core\Models.cs`）：`enum WallpaperKind{Video,Image,Web,Shader,Scene}`；`WallpaperItem{Id,Kind,Path,Name,Thumb,Volume,PlaybackRate,AudioReactive,Interactive,Props}`。
- `wwwroot\`：内置 Web 运行时（`index.html`+`runtime.js`+内置着色器/粒子壁纸），嵌入 exe 自解压（阶段二/三）。
- `SceneEditor\`：WebView2 场景编辑器页 + 宿主窗，产出 `scene.json`+素材到壁纸目录（阶段三）。
- `src\Views\WallpaperGalleryWindow.xaml(.cs)`：图库/选择器——缩略图（视频用 `FfmpegService.ExtractThumbs`）、导入、每屏分配、启用开关、进入编辑器。

## 修改文件
- `src\Core\Models.cs`：`AppState` 加壁纸设置（均带默认值、向后兼容）：`WallpaperEnabled`、每屏分配 `Dictionary<string,Guid> WallpaperPerMonitor`（键=显示器 DeviceName）、`WallpaperLibrary List<WallpaperItem>`、`WallpaperFpsCap`(int,默认60)、`WallpaperPauseWhenOccluded`(默认true)、`WallpaperPauseOnBattery`(默认true)、`WallpaperMuteOnFullscreen`(默认true)、`WallpaperGlobalVolume`(double)、`WallpaperAudioReactive`(默认true)。可空坐标一律 `double?`。
- `src\Services\DeskNestService.cs`：`Start()` 内 `wallpaper=new WallpaperService(this); if(State.WallpaperEnabled) wallpaper.Start();`；加 `ShowWallpaperGallery()`；`ApplyPreferences` 调 `wallpaper.ApplyPreferences()`；`Dispose/Exit` 清理。
- `src\Services\DeskNestService.State.cs`：`SanitizeState()` 用 `Finite` 钳制壁纸 double（音量 0–1、倍速 0.25–4、FPS 由 int Clamp 10–240）；`Load()` 如需迁移加迁移位。
- `src\Services\DeskNestService.Tray.cs`：`BuildTrayMenu()` 在“快速操作”区加 `Item(L("桌面壁纸","桌面壁纸","Live wallpaper"), ShowWallpaperGallery)`。
- `src\Views\SettingsWindow.xaml` + `.xaml.cs`：新增“壁纸”分页——**必须同步四处**：XAML 导航 `ListBoxItem` + 右侧分页 `Grid`、`Nav_Changed` 的 `pages[]`、`Nav_Changed` 与 `RefreshLanguage` 的 `names[]`；`LoadState()` 灌值 + `Changed()` 回写（沿用 90ms 防抖 `ApplyPreferences`→`Save`）。
- `src\Core\Localization.cs`：`Words` 加壁纸词条（zh-TW 键→(简中,英文)），如 `桌面壁纸/每屏獨立/音頻響應/遮擋暫停/電池暫停/幀率上限/新增壁紙/場景編輯器`。
- `src\Core\BeeXPaths.cs`：加 `WallpapersDir => Path.Combine(Root,"Wallpapers")`；并入 `EnsureLayout()` 与 `TopLevelDirs`（换根时自动迁移）。
- `src\BeeX.DeskNest.csproj`：镜像 Write，`EmbeddedResource Include="Wallpaper\wwwroot\**"` 且 `LogicalName=BeeX.DeskNest.wallpaper.%(RecursiveDir)%(Filename)%(Extension)`（阶段二起）。

---

## 关键技术决策（决策完整）

### 挂载（`DesktopWallpaperHost`）
1. `FindWindow("Progman",null)` → `SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)0x1, SMTO_NORMAL, 1000, out _)` 触发 WorkerW 生成。
2. `EnumWindows` 找到含子窗 `SHELLDLL_DefView` 的顶层窗，取其**兄弟** `FindWindowEx(IntPtr.Zero, thatTop, "WorkerW", null)` 为目标 WorkerW；找不到则回退 `SetParent` 到 Progman。
3. 每屏宿主 `SetParent(hostHwnd, workerW)`，按显示器物理像素定位到 WorkerW 客户区（WorkerW 覆盖整个虚拟桌面，子坐标相对其左上=虚拟屏原点）。
4. 扩展样式：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，默认加 `WS_EX_TRANSPARENT`（鼠标穿透到桌面，图标/右键菜单照常）；去 `WS_EX_APPWINDOW`；`WindowRegionHelper.HideFromAltTab`+`DisableSystemShadow`。
5. **自愈**：隐藏消息窗注册 `RegisterWindowMessage("TaskbarCreated")`，Explorer 重启时重挂载全部宿主；另 ~2s 校验 `IsWindow(workerW)`。
6. **管理员/UIPI**：本进程高完整性，`SetParent` 到中完整性 Explorer 的 WorkerW 属高→低操作，UIPI 允许；WebView2 在管理员进程可用（已由 Write/OCR 验证）。

### 每屏渲染（`WallpaperWindow`）
- 视频/图片：`MediaElement{LoadedBehavior=Manual,UnloadedBehavior=Manual,Stretch=UniformToFill}`，`MediaEnded` 回卷循环；音量/静音/倍速来自设置；同款壁纸多屏各自独立播放实例（稳，避免混合 DPI 单窗问题）。
- 网页/着色器/场景：`WebView2` + `CoreWebView2Environment.CreateAsync(null, Path.Combine(BeeXPaths.DataDir,"WallpaperWV2"))` + `SetVirtualHostNameToFolderMapping` 到自解压 wwwroot（仿 `WriteHost.EnsureWebAssets`，按 MVID 戳）；`DefaultBackgroundColor` 透明；`WallpaperRuntimeBridge` 下发数据。
- 显示器增删/分辨率变更：`DisplaySettingsChanged`+`WM_DISPLAYCHANGE` 重建宿主。

### 遮挡+电源调度器（`VisibilityGovernor`，可单测）
- 触发：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 即时 + 500ms 兜底轮询。
- 遮挡算法：对每屏，`EnumWindows` 取可见/未最小化/非工具窗的顶层窗，其矩形与屏矩形求交集作为遮挡；用**不相交矩形列表 + 逐个切割(guillotine split)**求并集面积；`visibleFraction = 1 - covered/monitorArea`；全屏判定=某前台窗恰好铺满该屏。
- 电源：`GetSystemPowerStatus` 取 `ACLineStatus`/`SystemStatusFlag`(节电模式)。
- 决策函数（纯函数、单测）：

```
int TargetFps(double visibleFraction, bool fullscreen, bool onBattery, bool saver, int cap, int refresh){
  if (fullscreen) return 0;                        // 全屏应用：暂停
  if (visibleFraction <= 0.01) return 0;           // 被完全遮挡：暂停（WE 此时仍满帧）
  if (saver) return 0;                             // 节电模式：暂停
  if (onBattery && pauseOnBattery) return 0;       // 电池：可暂停或降到 24
  return Math.Min(cap, refresh);
}
```

- 执行：0 FPS → 视频 `Pause()`+`WebView2.TrySuspendAsync()`；恢复 → `Resume()`+`Play()`；非零 → 向 JS `postMessage{type:'fps',value}`，运行时用 rAF 门控。

### 音频总线（`AudioSpectrumBus`，阶段二）
- 一路 `WasapiLoopbackCapture`，`DataAvailable` 累积 float 样本；每 ~16–33ms 取 1024/2048 窗，Hann 加窗，`FastFourierTransform.FFT`（NAudio.Dsp）→ 幅度 → 64 条对数频带（attack/decay 平滑）+ 能量法节拍检测；广播事件→各 Web 壁纸桥 `postMessage{type:'audio',bands,beat,level}`。
- 仅在“存在启用且未暂停的音频响应壁纸”时运行；默认设备变更时重建采集。

### Web 运行时 SDK（`wwwroot\runtime.js`，阶段二）
- `window.BeeXWallpaper`：`onTime(dt,t)`/`onAudio(bands,beat,level)`/`onPointer(x,y,down)`/`onResize(w,h,dpi)`/`onPause|onResume`/`onProperty(k,v)`。
- WebGPU 初始化，失败回退 WebGL2/Canvas2D；内置两张壁纸（Shadertoy 风格片元运行器 + 音频粒子场）。
- 可选 **WE-HTML 兼容 shim**：垫 `window.wallpaperPropertyListener`/`wallpaperRegisterAudioListener`，可直接加载部分 WE 网页壁纸。

### 交互式壁纸（阶段二）
- 默认 `WS_EX_TRANSPARENT` 穿透；交互模式用 `WH_MOUSE_LL` 低级钩子**只观测**全局光标（不吞事件），把归一化坐标/点击转发给 JS，桌面图标与右键仍正常。

### 场景编辑器（阶段三）
- WebView2 编辑器页：图层（图片/视频/粒子/文字/着色器）、属性面板、音频绑定（如把缩放绑定到低频带）；保存 `scene.json`+素材到 `WallpapersDir\<id>\`；运行时按 `scene.json` 渲染。

### 图库（`WallpaperGalleryWindow`）
- 列出 `WallpapersDir` 已装壁纸缩略图（视频首帧用 `FfmpegService.ExtractThumbs`）、导入（拷进目录）、每屏分配下拉、启用开关、“新建”进编辑器。

## 生命周期/边界
- 单文件自包含发布：WebView2 loader + wwwroot 嵌入自解压已被 Write 验证可行。
- 退出/禁用清理：`SetParent(host, IntPtr.Zero)` 解挂 + 销毁窗 + 停音频 + Dispose WebView2，桌面复原。
- 锁屏（`SessionSwitchReason.SessionLock`）暂停、解锁恢复。
- 录屏/截图：自抓帧走 `CopyFromScreen`，壁纸作为桌面一部分会入镜（符合预期）。

## 测试计划
- 新增单测（`test\`，仿 `Core\ModelsSerializationTests`/`BeeXExpressionTests`）：
  - `VisibilityGovernor` 矩形并集/差集面积（屏+遮挡矩形→可见比例）与 `TargetFps` 决策表。
  - 音频频带映射（给定幅度数组→64 带、节拍阈值）。
  - `AppState` 含壁纸新字段的 JSON 往返 + `SanitizeState` 钳制（音量/倍速/FPS 边界、NaN 回退）。
- 手动验证：单/多屏挂载与图标穿透、Explorer 重启自愈、全屏/遮挡/电池降帧、视频循环、网页 WebGPU 回退、音频律动、交互壁纸、换根迁移 `Wallpaper` 目录。
- 交付按既定流程（改需求→`dotnet test`→杀进程→`publish` 便携版→移除 pdb→重启，单条命令）。

## 假设
- 只支持本地壁纸文件与内置壁纸，不做在线商店/Workshop。
- 每屏可独立分配，也可“全部相同”。
- 渲染宿主在主进程内（复用服务/音频/桥，无 IPC）；如后续需崩溃隔离再提升为 `--wallpaper` 子进程（仿 `--cleaner`）。
- 需要 WebView2 Runtime（evergreen；Write 模块已依赖同一前提）。