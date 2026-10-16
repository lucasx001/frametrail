# 点击自动运镜与来源选择修复

日期：2026-10-16。环境：本机 Windows 11 x64 / build 26300。

## 行为与实现

- 编辑器不再显示「根据点击生成运镜」。准备胶囊新增默认开启的「点击自动缩放」图标开关，支持键盘、工具提示和无障碍状态，开启时显示绿点。准备条宽度调整为 418 × 56 DIP。
- 开始录制时固定开关；关闭仍可记录光标。关闭光标记录或使用 Android 时自动缩放不可用。录制期间不更改本次配置。
- 每个完成分段统一生成自动运镜，包括周期检查点、暂停、停止和主来源关闭；事件依各自媒体偏移映射至同一源时间，再由现有 C++ DogSC 规则规划器处理。重新生成不重复累积效果，手工编辑的效果继续受保护。
- 静止鼠标的 60 Hz 采样不再延长聚焦；只有实际位移及左右键按下计入活动。规划在工作线程执行，失败保留已完成源视频、既有运镜并发出提示。
- 动画非破坏性写入工程及恢复清单，原始视频保持全景；预览和导出复用共享渲染器。
- 选择遮罩按模式注册 Win32 光标：显示器/窗口为 `IDC_ARROW`，区域为 `IDC_CROSS`。显示器/窗口在同一目标的按下/释放后直接确认，重叠窗口按枚举顺序优先最前方可录制窗口；区域仍需拖框后确认。窗口模式初始悬停也按光标位置命中窗口。

## 自动验证

- Release 构建：0 警告、0 错误。默认输出被运行中的旧进程占用时，先在 `artifacts/click-zoom/app/` 独立输出验证。
- `scripts/test.cmd -Configuration Release -LockedRestore`：7 组原生 / 147 项默认托管测试通过。新增 10 项覆盖默认开启、开关关闭、光标关闭、无点击/范围外点击、静止退出、连续活动、暂停续录与恢复快照、规划失败及主来源结束。
- WinUI 自测：开关不改变光标设置；光标和 Android 依赖正确；明暗胶囊无裁切。实际创建选择遮罩并读取已注册光标，模拟按下/释放验证显示器与重叠窗口直接确认；十字区域拖框仍返回正确物理坐标。真实 WGC 短录制、标记、暂停/继续、停止和重开通过。
- 媒体专项：使用真实 3 秒编码素材和确定性光标轨，经过 `RecordingCoordinator` 正常开始/停止生成工程，无手动添加效果；重开后分别导出开/关两组 MP4。独立 FFmpeg 完整解码、各 90 帧 / 3 秒；缩放前/中/后共 6 组 GPU 预览与成片检查通过，最差平均 RGB 误差约 2.20 / 255。开启组聚焦时与全景明显不同，静止退出后恢复全景；关闭组始终保持全景。独立 GPU 解码的少量颜色舍入使用容差比较。

证据：`artifacts/click-zoom-final-build.log`（默认目录完整构建/测试）、`artifacts/click-zoom-tests.log`、`artifacts/click-zoom-app-build.log`、`artifacts/click-zoom/ui/ui-validation.json`、`artifacts/click-zoom/media-02/recording-motion.json`、`artifacts/click-zoom/media-02/independent-verification.json`。前一轮 `media/` 的哈希严格相等检查因不同 GPU 解码的少量颜色舍入失败，后续以原始 RGB、几何差异及编码容差独立核验，保留原产物。

复验媒体：运行 Workspace.Smoke 的 `--recording-motion <3秒640x360素材.mp4> <新的输出目录>`，然后运行 `python tests/FrameTrail.Workspace.Smoke/verify_recording_motion.py <输出目录>`；原生 DLL 使用同次构建产物。UI 复验使用 `FrameTrail.App.exe --ui-self-test <输出目录>`。

## 验证边界

来源选择的按下/释放由应用内测试发送给产品控制器，已验证注册的原生指针类型和选择结果；不声称完成跨应用物理鼠标点击、多屏/多 DPI、HDR 或 Mac 程序逐帧对照。真实 WGC 录制与确定性光标素材的预览/导出分别验证，未将后者描述为真实桌面点击采集验收。
