# FrameTrail 对照 Open Screen 的功能与结构审阅

日期：2026-10-25。FrameTrail 基线：`ffb401be0bdcaabfc66ee2408eb51d3ca84abf25`。
Open Screen 对照：`siddharthvaddem/openscreen`，`ca8104c279da0d4b4481ec992ff103949e22c772`。

2026-10-25 实施更新：用户选择仅执行 R1–R5 五项实现问题；五个独立 sub-agent 已交付，主 agent 完成集成审阅并补修缩略图取消边界。最终 Release、9 组原生/265 项托管、光标像素、独立媒体和三链窗口方法集成通过。实际鼠标/键盘因 Windows 锁屏未执行，保留待验收；新功能建议不纳入本轮。见 [0031 任务](../0031-review-fixes.md)、[主验证](../validation/0031-review-fixes.md)。下文保留实施前的审阅依据。

## 结论与范围

FrameTrail 应继续围绕 Windows 本地软件教程、产品演示、操作说明，以及 Android USB 演示展开。已有录制、恢复、非破坏性剪辑、自动运镜、画面包装和原生导出闭环。下一阶段优先修复跨分段的一致性与长录制成本，再补少量帮助观众看懂操作的功能。

Open Screen 的原仓库 README 已声明归档，并指向 `getopenscreen/openscreen` 社区延续项目。本报告以 FrameTrail 已采用壁纸的原仓库、固定提交作为对照，不混用其他 fork 的功能，也不假设 README 中的能力全部达到发行验收标准。

本次完成规格、待办、关键调用链、测试配置和上游相关源码的静态审阅，并运行本地自动测试。未启动两款应用进行交互对比，未进行跨 DPI、真实手机或长时音画同步验收。下面区分确定的代码逻辑问题、性能风险与产品建议；没有改动功能代码。

## 审阅发现（按处理优先级）

### R1 / P2：光标时间偏移在录制检查点处产生空窗

位置：`native/FrameTrail.Engine/media/renderer.cpp:103–137`。

渲染先用未偏移的源时间选取 pointer 文件（105–106），再对该文件的局部时间加 `cursorTimeOffset`，越界直接跳过（129–130）。因此无法从相邻连续分段取得偏移后的光标。

确定性例子：两段连续 pointer 轨分别覆盖 `[0,60)` 和 `[60,120)` 秒，偏移 `+0.2s`，播放时间 `59.9s`。第一段算出 `60.1s` 被拒绝；第二段又被未偏移的 `59.9s` 排除，最终不绘制光标。负偏移会在后一段开头出现类似空窗。共享渲染意味着预览、导出都会经过此逻辑。这是源码推导，尚未制作像素复现样片。

建议：先计算偏移后的源时间，再在同一个连续录制区间内解析 pointer 轨；明确禁止跨被删除片段和真正暂停边界的插值。补正负偏移、60 秒物理检查点、剪切边界的渲染回归。不要通过扩大当前分段时间范围掩盖问题。

同一路径还有连续性问题：每个 pointer 文件首样本都重新设置 activity（123–124），平滑历史和点击反馈也仅来自当前文件。开启静止隐藏时，跨连续检查点可能重新显示静止光标；边界前的点击波纹可能提前结束。宜一起建立跨物理分段的光标取样上下文。

### R2 / P2：自动运镜全量重算位于录制状态转换锁内

位置：`src/FrameTrail.Workspace/AutomaticZoomPlanner.cs:16–45`；`RecordingCoordinator.cs:226–241,272–293`。

每次 checkpoint 都遍历所有历史 pointer 文件、解析 JSONL，再重新生成完整自动运镜。默认每 60 秒执行一次，累计扫描量随检查点数量近似二次增长。Monitor 持有 `transition` 信号量等待该流程；暂停、停止也要等待同一信号量。`Task.Run` 避免占用 UI 线程，却没有缩短这些操作等待锁的时间，而且当前保存 checkpoint 在规划之后。

规划事件有项目累计 100,000 条硬上限。非点击移动最多约 10 Hz；持续有效移动约 2 小时 47 分即可接近上限，点击会改变实际到达时间。到限后捕获异常、保留此前运镜和素材，但新区域的自动运镜无法继续生成。不是已观察到录制丢帧；实际延迟仍需基准测试。

建议：按完成分段缓存活动摘要，保留尾部尚未闭合的活动窗口，仅重算受影响区间。可恢复素材 checkpoint 优先落盘，规划作为可取消、可重建的派生任务，再以修订号提交结果。自动生成的稳定区间不重复读取。验证 30/60/180 分钟高活动输入的内存、规划耗时、停止等待与失败恢复；保留当前对人工修改的保护。

### R3 / P2 优化：有声导出仍先生成完整临时 PCM

位置：`native/FrameTrail.Engine/media/export.cpp:119–159`，`internal.hpp:33`。

`mix_audio(wav)` 在 `BeginWriting` 前完成，然后导出再读取 WAV 编码 AAC。播放已经流式化，导出尚未复用这一优势。48 kHz、双声道、16 bit 的混音临时文件约占 192,000 字节/秒，即每小时 691.2 MB（约 659 MiB）；再叠加源音轨和输出视频，对长教程不友好。RF64 解决文件尺寸格式限制，不减少这部分等待和磁盘使用。

建议：抽取可顺序拉取 PCM 块的混音接口，供播放和离线编码复用，保持 WSOLA 状态连续、源时间映射、取消和进度报告。验收剪切、变速、双音轨、取消清理与音频首尾，不能只以内存不增长判定成功。这项已在 todo 中，应作为现有优化推进，不能包装成新发现的缺失功能。

### R4 / P2 维护性：partial 文件拆开了代码，没有拆开编辑器职责

位置：`EditorWindow.xaml.cs`、`EditorWindow.LiveVisuals.cs`、`EditorWindow.LiveProject.cs`、`EditorWindow.Assets.cs`、`PreviewSurface.cs`。

当前有 24 个 `EditorWindow*.cs` 文件，包含生产和验证代码。窗口同时持有编辑事务、播放时钟、选择状态、导出、素材后台任务、项目切换和若干 busy 标志。跨 partial 的字段依赖使保存、取消、关闭的组合路径难以独立测试。

建议渐进抽取：

1. `EditorTransportController`：播放、暂停、seek、悬停预览、音频停止屏障与 generation。
2. `EditorEditController`：拖动事务、延迟提交、撤销、dirty/save 状态。
3. `EditorAssetCoordinator`：缩略图、峰值缓存、取消、请求去重。
4. `ExportJobService`：冻结快照、进度、取消和产物提交。

窗口保留 UI 绑定与输入转发，继续使用已有 `EditorSession`、`LatestRequestQueue`、`PlaybackFrameGate`。不建议整仓重写 MVVM，也不建议为了降低文件行数继续增加 partial。关键异步流程和原生导出中的多语句单行应展开，使锁、异常和资源生命周期可审阅。

### R5：测试层级和产品规格需要对齐

`tests/FrameTrail.App.Tests/FrameTrail.App.Tests.csproj` 将若干 App 辅助源文件链接进 net8.0 测试程序集，没有引用实际 WinUI 窗口。它能验证队列、缓存、几何、目录等逻辑，但不能代表编辑窗口事件顺序、控件命中和窗口关闭已验证。应用 self-test 仍有价值，不过直接调用处理器不能替代真实指针和键盘操作。

建议保留 CPU 单测，逐步把窗口中的控制流程抽成可测试对象；另外在登录桌面的机器上固定三条 UI 冒烟链：录制→停止→编辑→导出，拖动效果→撤销→重开，播放/后台任务运行时切换和关闭工程。

`docs/spec.md` 3.3 后的 M3 说明仍称点击反馈、静止隐藏和偏移未实现；但 `EditorWindow.Cursor.cs`、`renderer.cpp:129–151` 和 `CursorTests.cs` 已有实现与契约测试。README 也残留编辑器中“根据点击生成运镜”的旧入口描述，与规格中的录制时自动生成不一致。应明确当前实现和历史阶段记录，避免据旧文档重复立项。

## 功能对照与取舍

| 方向 | 当前 FrameTrail | Open Screen 参考 | 对 FrameTrail 的决定 |
|---|---|---|---|
| 屏幕/窗口、麦克风/系统音、摄像头 | 已有原生路径，另有区域/进程音频/Android USB | Windows 同样有原生 WGC 桥接 | 保留当前技术路线；不移植 Electron 采集链 |
| 自动缩放、平滑、人工修正保护 | 已有点击活动规划、focusPoints、edited 标记 | 停留点建议、跟随模式、自动/手动来源 | 改善可控性与成本，不重复做自动缩放 |
| 光标点击反馈、静止隐藏、偏移 | 已有 | 光标主题、路径平滑和点击动效 | 先修分段一致性；更多主题优先级低 |
| 背景、裁切、比例、样机、预设 | 已有；18 张相同壁纸已纳入工程 | 壁纸/渐变/背景、比例、透视 | 无需再做相同图库或另一套检查器 |
| 标注 | 图片与固定风格文字贴图，可直接拖动 | 文本/箭头/图片，文本动画 | 最值得补的是原生箭头、步骤编号、少量文字样式 |
| 摄像头构图 | 可拖动、形状、镜像、独立动画 | 缩放时自动缩小；画中画/上下/并排预设 | 增加可选“运镜时缩小”，及少量可编辑构图预设 |
| 遮挡 | solid/pixelate/blur，screen/canvas 附着 | 有 blur 代码，但 `BLUR_REGIONS_ENABLED=false` | 强化自己的隐私遮挡入口；不把上游隐藏功能当成熟标杆 |
| 导出 | MP4/M4A/工程素材副本，GPU/CPU 路径 | MP4/GIF | 优先选区 MP4 导出；GIF 为短演示可选后续 |
| 字幕 | 当前规格明确排除识别 | 本地 Whisper tiny Worker，打包模型 | 后置；先考虑 SRT 导入和手工修改，再做可选离线识别 |
| 快捷键 | 已有编辑快捷键；录制停止/标记可选键 | 可配置快捷键 | 后续统一 command registry，再提供编辑快捷键重映射 |

## 最适合投入的功能

### 1. 教程标注：箭头、步骤编号、文字说明

场景：说明“点这里”“先做这一步”，避免为简单箭头导入图片。复用现有 source effect、画布拖动、时间线、撤销和共享合成；增加向量箭头端点、颜色/线宽、编号徽标、文字字号/颜色/背景。先实现静态与简单淡入，不扩展成通用图形编辑器。原生 Direct2D/DirectWrite 渲染，而非生成位图供不同分辨率拉伸。

验收：剪切和变速后区间仍正确，屏幕附着随运镜移动、画布附着固定，预览/1080p/4K 导出一致，中文换行可读，拖动一次对应一次撤销。

### 2. 可控运镜与摄像头让位

场景：放大菜单或按钮时，头像不挡住重点。先提供低/中/高运镜强度、选中缩放段“固定焦点/跟随光标”，以及独立开关“缩放时缩小摄像头”。这是在已有运镜上增加控制，不恢复此前已移除的全局“重新生成”按钮。

摄像头让位可参考 Open Screen 的逆缩放和最小尺寸限制，但参数需按 FrameTrail 画布验证。先固定锚点缩小，暂不自动把头像跳到另一角。所有结果以绝对内容时间求值；切换帧率、随机 seek、导出不能依赖之前播放过哪些帧。Android 无光标输入时不伪造跟随能力。

### 3. 选区导出，随后才是 GIF

场景：一段长教程中，只把 8 秒操作发给同事、放进 issue 或文档。增加时间线 In/Out 或“导出选中片段”，从冻结快照生成导出视图，不修改原工程。继续优先 MP4；GIF 后续可提供尺寸、帧率、时长/体积提示，并复用同一帧渲染管线。

Open Screen 的直接参考是 GIF；选区 MP4 是针对 FrameTrail 长录制场景提出的产品适配建议，不声称来自上游现成功能。

### 4. 强化已有隐私遮挡的可发现性

将“遮挡敏感内容”作为容易找到的动作，复用已有 mask 和画布手柄；提供纯色/像素化快捷预设、清楚的持续区间。先解决用户找不到已有能力的问题。窗口位置改变时如何跟随属于独立后续，不立即引入目标追踪或 AI。

## 结构上应保留的优势

- `App → Workspace → Interop → C++ Core/Media` 的大方向合理：C++ 时间线映射和效果求值具有权威性，避免再在 C# 重建第二套语义。
- 预览/导出共享合成，效果按源时间保存；随机 seek 的确定性已有原生测试。借鉴上游交互时必须延续这点。
- 工程写入队列、原子替换、独占访问、恢复检查点、导出 staging、GPU sample 所有权与回退路径值得保留。
- 撤销已有数量/容量上限、手势合并；缓存与后台队列也已有界限。不建议无证据地重写成事件溯源系统。
- Open Screen 也有大型 `VideoEditor.tsx` 与 IPC handlers。可以借鉴其纯函数模块（布局、运镜建议、动画），不要把其组件组织当成无条件更优的架构模板。

## 建议推进顺序

1. 修 R1 跨分段光标，并同步规格中的过时状态；补边界媒体验证。
2. 改进 R2 自动运镜增量处理与保存时序；完成高活动长录制基准。
3. 围绕标注功能抽出最小编辑控制接口，交付箭头/编号，避免先做大规模重构。
4. 运镜控制、摄像头让位与选区导出按独立小任务推进；流式导出音频作为长工程性能任务。
5. 清理验证代码与生产控制流程边界，落实真实 UI 冒烟和已有硬件/发行待验收项。
6. 有明确需求后再排 SRT、离线识别、GIF、快捷键定制。暂不投入云账号、多主视频轨、专业调色、大量炫技动效或跨平台迁移。

## 验证记录

- 执行 `scripts/test.cmd -Configuration Release -NativeBuildDirectory artifacts/native-release -LockedRestore`：8/8 原生测试组通过；托管 App 49、Capture 6、Domain 111、Workspace 75，共 241 通过；Audio 托管测试被硬件过滤条件排除，没有执行。
- 测试成功不覆盖本报告 R1 的跨分段像素行为，也不证明 R2 长录制的交互时延。
- 另执行 `scripts/build.cmd -Configuration Release -NativeBuildDirectory artifacts/native-release -NativeOnly`：原生增量构建成功，8/8 原生测试组再次通过；没有执行完整 WinUI 应用构建或交互验收。
- 审阅期间工作树出现 `ProjectLibraryWindow.cs` 和 `ProjectLibraryWindow.Validation.cs` 的外部改动，本次未修改这两个文件；项目库相关改动不纳入本报告的基线结论。

## 上游源码索引

均固定到对照提交，避免后续分支变化影响结论。

- [仓库状态与功能说明](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/README.md)
- [自动运镜建议与重叠排除](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/components/video-editor/timeline/zoomSuggestionUtils.ts)
- [光标跟随与内容时间校正](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/components/video-editor/videoPlayback/cursorFollowUtils.ts)
- [摄像头让位和布局预设](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/lib/compositeLayout.ts)
- [标注模型](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/components/video-editor/types.ts)
- [文字动画纯函数](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/lib/annotationTextAnimation.ts)
- [GIF 导出](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/lib/exporter/gifExporter.ts)
- [字幕 Worker 与本地模型](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/lib/captioning/transcribe.worker.ts)
- [模糊功能开关](https://github.com/siddharthvaddem/openscreen/blob/ca8104c279da0d4b4481ec992ff103949e22c772/src/components/video-editor/featureFlags.ts)
