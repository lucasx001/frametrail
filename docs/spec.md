# FrameTrail 产品实现规格

状态：M0–M3 本机功能已实现；M4 可靠性修复与开发候选分发、M5 Android USB 实现及本地自动验证已完成；USB 真机、长时同步、扩展硬件、正式签名和干净机器安装升级仍待验收。规格更新日期：2026-10-16。

## 1. 产品定位与范围

FrameTrail 是面向 Windows 11 的原生录屏与轻量剪辑工具，用于制作产品演示、软件教程和操作说明视频。用户完成来源选择后录制屏幕、声音和摄像头，再通过非破坏性剪辑、光标运镜与画面包装生成成片。

这是独立的 Windows 产品，媒体引擎与应用层使用原生 Windows 技术实现。按用户要求，整体 UI 以相邻 `dogsc` 的截图与 SwiftUI 设计源码为一比一复刻目标，沿用其布局、尺寸、配色、字体和图标几何；Windows 原生窗口按钮与系统文件选择器保留平台行为。设计复刻不代表提前实现 M3 的视觉能力。FrameTrail 为工作名称，尚未做商标或域名核验。

### 1.1 支持范围

- 首版目标：处于微软支持周期内的 Windows 11 x64。API 最低 build 冻结为 22621，编译使用 SDK 26100；此下限不表示继续支持已结束服务的 Windows 版本。确切工具版本见 [toolchain.md](toolchain.md)。
- 不承诺 Windows 10、Windows ARM64、远程桌面和虚拟机环境的首版支持。
- 首版成片以 SDR、H.264 + AAC、MP4 为主，输出 1080p/1440p/4K、30/60 FPS；高分辨率与帧率组合受设备能力限制，运行时检测并清楚提示。
- 不承诺任意机器上 4K60、所有输入格式可解码或所有编码路径均为硬件加速。
- 数据默认本地保存，无账户、云上传或 AI 服务依赖；更新检查不得上传项目内容。

### 1.2 交付阶段

| 阶段 | 范围 | 完成任务 |
|---|---|---|
| M0 工程基础 | 可构建原生窗口、引擎桥接、领域测试框架 | 0001–0002 |
| M1 录制原型 | 屏幕、系统音、麦克风、光标、摄像头及可恢复项目 | 0003–0005 |
| M2 最小编辑闭环 | 共享渲染、基础时间线、MP4/M4A 导出 | 0006–0008 |
| M3 完整视觉能力 | 自动运镜、视觉效果、场景预设 | 0009 |
| M4 可分发版本 | 故障恢复、性能与硬件矩阵、安装更新验收 | 0010–0011 |
| M5 Android USB 扩展 | 数据线连接 Android 手机、设备画面与声音采集、接入现有工程 | [0012](0012-android-usb-recording.md) |

移动设备录屏需求改为“通过 USB 数据线连接 Android 手机后录屏”；FrameTrail 不规划 iPhone/iPad 有线采集。沿用原规格的阶段边界，Android USB 录屏属于首版后的 M5 扩展，不影响当前 M0/M1 的实现与验收结论。HDR 成片、专业调色、字幕识别、多主视频轨拼装、直播推流、插件市场、云协作仍不属于首版范围。

### 1.3 当前实现与待验收边界

- M0：原生 WinUI 窗口、C ABI / SafeHandle、严格 schema 1、权威 C++ 时间线/基础场景求值、构建脚本和 CI 配置已实现。原生与托管本地测试通过；实际云端 CI 和 Mac Swift 程序对照未执行。
- M1：WGC 显示器/窗口/区域、MF H.264 源视频、WASAPI 系统音/进程树/麦克风独立 WAV、摄像头采集接口、光标 JSONL/形状素材、标记、暂停/继续、工程保存/重开/恢复和原生录制工作区已实现。
- 工程、录制、恢复及本机 UVC 摄像头连续分段已有真实短测；多显示器/不同 DPI、其他摄像头、拔插设备、HDR 状态变化、30 分钟真实音画同步仍须实机验收。不能用短录制或时钟数学测试替代这些检查。
- 录屏 UI 参照 DogSC 的悬浮胶囊、来源轮盘、输入弹出面板和录制条，使用 WinUI 实现浅色/深色外观。显示器/窗口使用遮罩选择，区域在单个显示器内拖框后返回物理像素坐标；摄像头实时预览与镜像呈现仍随后续预览任务交付。原生鼠标命中与跨 DPI 的完整人工验收仍待完成，见 [录屏 UI 验证](validation/0005-recorder-ui.md)。
- 每 60 秒建立可恢复分段。重叠启动下一段，以其首帧 QPC 裁定上一段的逻辑尾部；物理源文件可保留不被工程引用的多余尾帧。摄像头分段共用持续采集源和独立文件编码器，延续边界前最近画面；自动 checkpoint 不重开设备，暂停则释放设备。启动下一段失败时当前录制继续。
- 录屏硬件编码器支持 D3D11 时通过有界 GPU NV12 样本池交给 MF writer，保留 CPU 回读兼容路径及阶段计时。受控 1080p 动画窗口 30 秒实测约 59.23 FPS、队列丢帧 0；先前 44.14 FPS 的 10 分钟负载环境不同，尚未完成同条件长测。详见 [连续性与性能记录](validation/capture-continuity-performance.md)。
- M2：共享 D3D11/Direct2D 合成、MF 解码、SwapChainPanel 预览、非破坏性剪辑/撤销、自动保存、保持音调的 WSOLA 混音，以及 MP4/M4A/源素材副本导出已接通。录制源 MP4 仍与原始音轨分开，成片由编辑器导出。验证范围见 [M2 记录](validation/m2-editor-export.md)。
- M3 高级运镜/效果已接入共享预览导出；M5 Android USB 已实现并通过模拟端的真实编码样本验证。M4 发行矩阵与 M5 USB 真机验收仍未完成，详见对应验证记录。
- 编辑器使用 DogSC 的 46 DIP 顶栏、点阵预览区、44 DIP 悬浮播放胶囊、340 DIP 右侧检查器与通栏时间线；按用户后续调整，六项导航及参数控件统一使用蓝色强调。背景素材子页签等宽铺满容器，文字居中。导出为 820 DIP 双栏面板，设置为 560 DIP 导航胶囊与页面岛。M3 已启用样机、场景预设、背景媒体与运镜；验证边界见 [整体 UI 记录](validation/dogsc-editor-ui.md)。
- 当前导出在 GPU 完成合成和 BT.709/NV12 转换，再使用有界 CPU readback 交给 MF writer；不能宣称整条编码链零拷贝或始终硬件编码。预览混音缓存使用 PCM WAV，超过 RIFF 4 GiB 会提示缩短工程，长项目缓存格式留待后续扩展。
- M0/M1 固定 .NET 8 验证基线；正式发行前升级受支持的 LTS，并重新验收。开发构建内置 Windows App SDK，仍需要 .NET 运行时，不能把单个 exe 视作完整发行包。

可重复命令与结果见 [进度](progress.md) 和 [主 agent 审阅记录](validation/m0-m1-review.md)。未完成的验收项保留在各任务清单中。

## 2. 用户流程与状态

1. 启动 → 选择显示器、窗口或区域 → 选择音频与摄像头 → 检查能力和权限。M5 增加 Android 手机来源，先完成 USB 连接、调试授权、设备选择与预览检查。
2. 开始 → 固定本次 RecordingPlan 和 RunId → 创建项目和恢复日志 → 等待有效视频帧与音频初始化 → 进入录制。
3. 录制期间支持暂停、继续、标记和停止；录制栏与备忘录尽量从本应用的采集中排除。
4. 停止 → 排空队列、结束文件、对齐轨道 → 校验素材 → 保存项目 → 展示完成页。
5. 打开编辑器 → 剪辑、调节画面与声音 → 自动保存 → 预览 → 导出。

主状态为 `Setup / Preparing / Recording / Pausing / Paused / Resuming / Finalizing / RecordingComplete / Editing`，失败与取消通过明确的结果对象回到可操作状态。导出有独立 JobId 与状态，不用录制状态表达导出进度。首版不允许开始录制时仍在导出，也不允许同一工程被两个可写会话同时打开。

关闭应用、切换项目和丢弃录制都必须等待对应持久化屏障；不能在后台保存未结束时移动或删除工程。

## 3. 功能规格

### 3.1 录制

- 当前来源：整块显示器、单个窗口、显示器内矩形区域。区域第一版不得跨显示器；M5 增加 Android USB 来源，具体约束见 3.5。
- Windows 桌面来源使用 Windows.Graphics.Capture 获取 D3D11 表面；区域在 GPU 上裁切，不以 CPU 截屏作为主路径。Android 来源通过独立 USB 适配器接收设备媒体流。
- 支持全局停止/标记快捷键；快捷键冲突需显示并允许重设。
- 系统声音默认指所选输出设备的 loopback，界面明确设备范围；另提供指定应用进程树音频模式，不能称为浏览器单标签音频隔离。
- 麦克风独立轨录制，提供输入电平；摄像头独立视频轨，支持设备、分辨率选择及预览镜像。
- 基于首个有效主来源视频帧建立录制时间零点，所有轨道映射至共同单调时钟；首帧前的音频裁除或调整需遵循固定策略。
- 暂停期间不进入成片时间；恢复时所有轨道使用同一累计暂停偏移。
- 光标独立记录位置、点击、可取得的形状与时间，主视频禁用内嵌光标。无法取得形状时使用明确的默认形状。
- 摄像头/麦克风断开可降级为缺失辅助轨并提示；主来源关闭或不可继续采集则结束录制并保留已完成数据。
- 对最小化窗口、锁屏、显示器断开、受保护内容和采集排除行为明确记录实测结果，不绕过系统保护。

### 3.2 编辑

- 一条逻辑主画面序列，可由多个录制分段组成；原始素材保持不可变。
- 分割、边缘裁剪、删除、重排、恢复原始顺序、片段变速，默认速率范围 0.25–4.0。
- 首版不允许同一源时间区间重复使用，避免映射歧义；编辑后至少保留一个有效主片段。
- 片段变速默认保持音调，需要验证选定时间伸缩实现；如未实现必须显式标注，不得默默采用变调播放。
- 摄像头、光标、录制标记和源时间关联效果随剪辑映射；剪切两侧不得生成跨越已删除内容的光标插值。
- 支持时间线缩放、播放头、逐帧定位、吸附、缩略图和音频波形。
- 撤销/重做、效果复制粘贴、手势合并为一次命令；撤销也要触发正常自动保存。
- 全局与片段级的系统音/麦克风音量、静音；暂不支持用户任意添加多个主视频源。

### 3.3 视觉与运镜

- 画布比例：自适应、16:9、9:16、1:1、4:3、自定义；比例与输出分辨率独立。
- 画面裁切、位置、缩放、留白、圆角、边框、阴影；窗口/浏览器/通用设备框。
- 背景：纯色、渐变、图片和循环视频；用户素材复制到工程内。
- 光标大小、平滑、点击反馈、静止隐藏、全局时间偏移。
- 自动缩放以点击和光标活动为输入；手动缩放具有进入、保持、退出阶段及曲线。用户手工修改后不能被自动重生成静默覆盖。
- 摄像头位置、大小、形状、镜像和出入场动画。
- 屏幕透视/倾斜、聚焦模糊、矩形遮挡/像素化/柔化、静态贴图、开场动画和场景预设。
- 画布上的选择框、辅助线和拖动手柄属于编辑 UI，不进入视频。
- 系统减少动画偏好用于应用界面；用户创作的视频动画不被该偏好改变。

M3（2026-10-15）已实现上述自动/手动运镜、纯时间曲线与平滑、HLSL 透视/区域效果、静态图片/文字贴图、摄像头形状和出入场、开场、工程内背景及视觉预设。参数、坐标附着、资源限制和遮挡顺序见 [schema](schema.md)，本机功能证据见 [M3 验证](validation/m3-motion-effects.md)。鼠标光标的点击反馈、静止隐藏和全局偏移仍属于后续扩展，当前 M3 只增加平滑，不将这些产品目标写成已支持功能。跨 GPU/长时/复杂工程性能仍按 0010 验收。

### 3.4 导出

- 默认 MP4：H.264 视频、AAC 音频；音频单独导出为 M4A；提供源素材复制导出。
- 输出调度用有理时间，按帧号计算采样时间；导出不按实时定时器推进，不因机器慢跳过输出帧。
- 支持取消、阶段进度、错误说明；预计剩余时间仅作估计。
- 输出先写目标旁的临时文件，成功 finalize 并校验后再替换目标；失败或取消不损坏已有目标文件。
- 导出固定项目修订快照，开始前确认待保存数据与素材可用；任务中禁止修改该工程，其他 UI 保持响应。
- 导出超时监测基于实际工作进展，不能用单一总时长阈值误杀长视频。

### 3.5 Android 手机 USB 录屏（M5，待真机验收）

- 连接方式限定支持数据传输的 USB 数据线；引导用户开启开发者选项与 USB 调试，并在手机端接受本机调试授权。Windows 缺少对应 ADB 驱动时提供官方安装指引，不自动安装驱动。
- 列出通过 USB 连接的物理手机，区分未连接、未授权、离线、可用与不兼容状态；多设备时必须明确选择，不自动选中模拟器或无线设备。每次仅一台手机作为主画面来源，不同时合成 Windows 桌面和手机两个主视频源。
- 视频兼容性下限按 scrcpy 的 Android 5.0 / API 21 评估，正式支持名单由实机矩阵确定。默认协商 SDR H.264，提供分辨率、码率及 30/60 FPS 上限设置；不把协商上限当作实测帧率。
- 手机内部声音与 Windows 系统声音分开命名、保存和控制。scrcpy 音频转发要求 Android 11+，Android 11 启动时需解锁；低版本、ROM 或应用限制导致不可采集时，明确提供无声录屏或 Windows 麦克风旁白，不能把电脑 loopback 当作手机音轨。
- 开始前显示手机预览和可用音轨；录制后复用暂停/继续、标记、周期分段、工程恢复、剪辑与导出。手机预览声音默认关闭监听，避免与电脑系统音重复采集。
- 初次实现锁定录制开始时的方向与输出尺寸；检测到旋转引起源尺寸变化时安全停止并保留素材，提示用户调整方向后新建录制。动态旋转连续录制留待后续验证。
- USB 拔出、授权撤销、设备离线或服务退出时，停止主来源录制、结束可结束的文件并保留已完成分段；重连后由用户重新开始，不静默把不同会话拼成连续素材。
- 仅录屏，不默认启用手机远程控制、剪贴板同步或手机麦克风。Windows 光标不叠加到手机内容；不能把电脑鼠标事件当成手机真实触摸事件，也不承诺自动点击运镜。
- 受保护内容可能无法采集，不绕过系统或应用保护。成功录制的素材必须复制进工程，重开和导出无需连接原手机；设备完整序列号不得写入普通诊断日志。

### 3.6 Android 接入方案约束

采用 ADB 负责 USB 发现、授权状态、设备服务启动与隧道；复用 scrcpy 的 Android 原生采集服务，并由 FrameTrail C++ 适配器接收带配置、关键帧与时间戳的媒体包。Windows 侧继续使用 WinUI 3、C++、Media Foundation 和 D3D11，手机画面直接进入原生媒体链路。

M5 固定 ADB 36.0.0 / 1.0.41（构建 13206524）、scrcpy server / 协议解析器 3.3.4，并校验官方归档及实际运行文件。scrcpy 协议属于内部协议，不承诺跨版本兼容；不能按一个版本的说明解析另一个版本。原型可用官方工具验证设备，正式接入须直接消费媒体流，不能把录制外部投屏窗口作为交付方案。C++ 使用 Media Foundation 解码预览与 H.264 直封 MP4，D3D11 承载画面；手机音频选择 48kHz 双声道 PCM，按设备 PTS 对齐并独立保存 WAV。WinUI 的手机声音控制沿用主声音增益，标签明确为手机内部音频；首轮不同时采集 Windows loopback。具体任务、时钟、schema 升级与验收见 [0012](0012-android-usb-recording.md)。

## 4. 技术栈与职责

| 模块 | 选择 | 责任 |
|---|---|---|
| App | WinUI 3、C#、XAML、稳定版 .NET/Windows App SDK | 窗口、MVVM、命令、项目工作区、保存调度 |
| Engine/Core | C++20 | 项目校验、TimelineMap、AutoZoomPlanner、FrameScene 求值 |
| Engine/Capture | C++/WinRT、WGC、WASAPI、Media Foundation | 视频/音频/光标采集与时钟 |
| Engine/AndroidCapture（M5） | C++ USB 来源适配器、ADB、配套版本 scrcpy server | 手机发现/授权、设备媒体流、时间戳映射与断线处理 |
| Engine/Render | D3D11、HLSL、Direct2D、DirectWrite | 共同的 GPU 渲染与素材栅格化 |
| Engine/Media | Media Foundation、必要的经评估音频处理组件 | 解码、音频混合/重采样/时间伸缩、编码与封装 |
| Bridge | C ABI + P/Invoke、SafeHandle | 生命周期、命令、状态、取消；禁止跨边界传视频像素数组 |
| Persistence | System.Text.Json、文件目录、串行保存队列 | 项目快照、恢复日志、版本迁移 |
| Tests | .NET 测试 + C++ 测试框架（0001 冻结） | 算法、ABI、持久化、媒体集成与回归 |
| Build | Visual Studio Build Tools/MSBuild + CMake | UI 与原生引擎构建，锁定 SDK/依赖版本 |

C# 保有权威的可编辑项目快照；C++ 接受带 revision 的不可变快照并返回计算结果或编辑结果。源/输出时间映射与效果求值只在 C++ 实现，不能在 C# 再写一套。JSON 是持久化/低频快照协议，不是逐帧 IPC 协议。

Media Foundation 负责媒体读写与编解码，不承担完整剪辑器语义。片段调度、seek 后解码至目标帧、播放时钟和效果求值均由项目引擎负责。硬件编码需探测，软件路径需明确分辨率/速度限制。HEVC 与其他额外格式暂不作为必须能力。

## 5. 整体架构

```text
WinUI App / ProjectWorkspace / EditorCommands
             │ versioned commands & immutable snapshots
             ▼
C ABI Bridge ── EngineSession
                 ├── Core: TimelineMap → FrameSceneEvaluator
                 ├── Capture: WGC / WASAPI / Camera / Pointer
                 ├── AndroidCapture (M5): ADB USB tunnel / scrcpy server
                 ├── Media: Decode / AudioMix / Encode
                 └── Render: shared D3D11 compositor
                              ├── preview swap chain
                              └── offline output texture → encoder
```

### 5.1 线程与资源

- UI Dispatcher 不做设备枚举、磁盘大文件 IO、解码或 GPU 等待。
- 采集回调只接收和入队；用有界队列与明确所有权管理纹理/音频缓冲。
- WGC 帧归还后不能继续持有其底层表面用于异步编码；复制到自有纹理或实现经验证的生命周期转交。
- GPU 命令提交由明确线程管理，纹理池有上限；同一适配器优先，跨 GPU 路径单独记录成本。
- 预览可合并过时请求；录制保留实际时间戳并统计丢帧；离线导出保证输出帧序列完整。
- RunId、SessionId、ExportJobId 和 Revision 拒绝过期回调。Native 异常不得穿过 ABI，错误通过稳定错误码及诊断信息返回。
- 捕获异常不能自动提升权限；设备丢失后重建资源或安全终止，禁止继续写无效样本。

### 5.2 时间与坐标

- 捕获时钟基于 QPC，持久化为版本化的整数 tick + timescale；音频保存采样率和样本偏移，不能长期依靠浮点累加。
- 建立设备时钟到会话时钟的映射并监测漂移；只有需要时采用受控重采样修正。
- Android 来源以设备媒体 PTS 为依据，建立到 QPC 的映射并保留音视频相对时间；USB 包到达时间只用于抖动/延迟诊断，不能替代媒体时间戳。手机与电脑麦克风的长期同步必须另做实测。
- 明确 screen physical pixels、window client pixels、DIP、normalized content、output pixels 五种空间，禁止隐式混用。
- 进程采用 Per-Monitor DPI Awareness V2；多屏负坐标、125%/150%/200% 缩放都有验收案例。
- FrameScene 是某一成片时间的画面定义，预览与导出使用相同图层顺序、动画和颜色规则。
- 首版 SDR 输出约定 BT.709；输入 HDR 需探测，完成 tone mapping 验证前明确阻止不支持的路径，不能静默生成泛白视频。

### 5.3 持久化

工作工程是 `.frametrailproject` 目录，Windows 不将目录视为 macOS bundle；应用从目录内的 `project.json` 打开，首版不假装实现单文件双击。后续如引入便携归档，必须解包至工作目录后编辑。

```text
Example.frametrailproject/
  project.json
  project.previous.json
  media/                  # 带类型和稳定 ID 的原始轨道/分段
  events/pointer.jsonl
  assets/                 # 图片、背景、预设引用素材
  recovery/manifest.json
  recovery/diagnostics.jsonl
  cache/                  # 可重建的缩略图、波形、代理
```

- 独立 schema 从 1 开始，不使用 DogSC 的版本号暗示直接兼容。
- Android 录制使用 schema 2，增加 `primarySource` 和独立 `androidAudio`；旧桌面工程继续按 schema 1 严格读写，提供显式无损升级。手机音轨不复用 `systemAudio`，Android 工程拒绝 Windows 光标与 loopback 轨；见 [schema.md](schema.md)。
- project.json 包含 source manifest、轨道偏移、剪辑序列、效果、导出设置及 revision；所有路径为工程内相对路径。
- 通过 session epoch 和 revision 的串行保存实现去抖自动保存、最终 flush、上一版快照和同卷替换。
- 拒绝路径逃逸和未知未来版本，缺素材提供恢复/定位提示；不能静默删除不认识的字段后覆盖工程。
- 录制按可独立结束的分段保存并写恢复清单，允许恢复已 finalize 分段；不承诺修复任意损坏的最后一个 MP4 分段。
- DogSC 工程导入不是首版要求；未来转换器需独立验证字段、媒体格式、字体和平台资源。

## 6. 性能与验收原则

以下为目标而非已经测得的结果。任务 0003 记录基线机器 CPU/GPU/RAM/驱动/系统 build，任务 0010 固化测试矩阵。

- 基线机器进行至少 30 分钟 1080p60 动态画面录制，统计实际捕获/编码帧数、丢帧、队列水位、CPU/GPU、内存和音画偏差；目标首尾音画误差均不超过 50ms。
- 静态桌面采集不一定每秒产生 60 个不同画面；分别报告回调率、内容变化和输出帧率，不能把补帧当作捕获性能。
- 录制与预览有界内存；长时测试不出现持续无界增长，纹理/句柄在关闭会话后回到稳定水平。
- 1080p 典型项目预览目标为 60 FPS；复杂效果可以降低预览分辨率，导出质量不跟随降低。
- 自动测试覆盖剪辑边界、重排变速、光标断点、动画求值、存储排序和导出取消。
- GPU 画面使用容差/图像指标比对，不要求不同 GPU 的浮点结果逐字节一致。
- 构建成功、自动测试通过、实机功能通过、安装升级通过分别记录；任何未验证项保持未勾选。

## 7. 发布与质量

- 使用签名安装包，首选 MSIX + App Installer 路线，在 0011 验证系统支持、证书信任、原生 DLL 和更新行为后冻结。
- 首版至少中文与英文，支持键盘导航、可访问名称、高对比度和合理焦点行为。
- 日志只记录诊断与资源指标，不记录键盘文本、截图或完整工程内容；导出日志由用户主动选择。
- 依赖需固定版本、记录许可证；涉及第三方代码/素材复制时保留来源和许可证。FFmpeg 若引入，仅作为经过评估的媒体能力组件，不替代产品原生架构。
- 不自动安装驱动、购买证书、发布版本或上传用户素材。
- M4 开发候选提供自包含 x64 ZIP 与未签名 MSIX，具体构建、校验和已知限制见 [M4 验证](validation/m4-reliability-distribution.md)。关于页只查询更新，不执行安装或结束进程；可选 App Installer 描述文件不声明启动/后台自动安装。正式签名与干净机器验收完成前不将开发候选称为正式分发版本。
- M5 安装更新需重新验收 ADB/scrcpy 组件、许可与校验、干净机器 USB 连接和与已有 ADB 工具共存；完成 0011 不等于 Android 发行验收已通过。

## 8. 技术依据

- [Windows Graphics Capture](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [Application Loopback](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)
- [D3D11 与 Media Foundation](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation)
- [SwapChainPanel](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel)
- [排除本应用窗口采集](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [ADB 与 USB 调试授权](https://developer.android.com/tools/adb)
- [Windows OEM USB 驱动](https://developer.android.com/studio/run/oem-usb)
- [scrcpy 官方项目与设备要求](https://github.com/Genymobile/scrcpy)
- [scrcpy 音频兼容性](https://github.com/Genymobile/scrcpy/blob/master/doc/audio.md)
- [scrcpy 服务与内部协议](https://github.com/Genymobile/scrcpy/blob/master/doc/develop.md)

以上依据用于技术选型；每个任务实现前仍需核对所选稳定 SDK 的能力和最低版本。

### 2026-10-16 M5 实现边界

按用户要求，在 M4 扩展验收之前推进 M5。本地已接通 USB 类型枚举、授权引导、原生媒体流、预览、分段/恢复、schema 2 与编辑导出；固定方向使用 scrcpy 的 `capture_orientation=@`，编码配置变化时结束本轮录制并要求重新连接。

自动验证使用真实编码样本与模拟 scrcpy 对端，覆盖竖屏、协议故障、暂停/继续、进程终止恢复和离线编辑导出。当前没有连接手机；Android 版本/ROM、OEM 驱动、受保护内容、30 分钟手机音视频与电脑麦克风偏差、干净机器及安装更新均未验收。PTS 与 QPC 的首帧映射及到达偏移只用于时序基准/诊断，未宣称消除了 USB 固定延迟或证明跨设备同步达到 50ms。详细证据见 [M5 验证](validation/0012-android-usb-validation.md)。
