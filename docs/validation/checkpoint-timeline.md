# 连续录制的时间线检查点修复

日期：2026-10-18。环境：本机 Windows 11 x64。

## 原因与行为

录制默认每 60 秒生成一个可独立完成的恢复文件。此前 `RecordingCoordinator.CompleteSegmentAsync` 每完成一个文件就追加一个 `RecordingSegment`，把恢复文件的边界变成了编辑器里的切点，因此连续录制一两分钟也会出现多个片段。

现在自动检查点延长当前编辑片段，并保留其稳定 ID；底层 MP4、声音、光标等文件仍独立保存，恢复清单和原始素材偏移保持原逻辑。主动暂停 / 继续仍创建新的编辑片段。停止后打开编辑器，连续录制默认是一整段，用户仍可自行分割、裁剪和撤销。已有工程不自动合并，避免覆盖用户剪辑。

## 验证

- `scripts/build.cmd -Configuration Release -LockedRestore`：Release 0 警告 / 0 错误，7 组原生测试、212 项默认托管测试通过（App 30、Capture 6、Domain 111、Workspace 65）。音频外设用例仍按默认配置排除。
- 新增 3 项协调器回归：默认 60 秒检查点连续触发一次 / 两次后，多个媒体文件只映射为一个完整片段；停止、重开、稳定 ID、原生边界前后选源、手动分割 / 撤销、跨边界裁剪和仅完成检查点恢复通过。另验证「自动检查点后主动暂停 / 继续」只保留用户主动产生的切点。
- 原有摄像头检查点测试继续验证先启动下一段再结束上一段，并分别断言两个媒体文件、一个编辑片段；Android smoke 将物理文件数与编辑片段数分开检查。
- 真实 WGC 录制 **125.2900407 秒**，使用默认 60 秒周期，生成 **3 个已完成主画面文件、1 个稳定编辑片段**。停止后重新打开，编辑器时间线覆盖完整源时长；两个文件交界前后的原生 GPU 取帧均成功。
- 在第一个物理文件交界处手动分割后撤销，恢复一个片段；跨交界裁剪 / 导出 2 秒视频后再撤销，恢复完整录制。独立 FFprobe 确认 H.264、640×360、30 FPS、60 帧 / 2 秒，FFmpeg 完整解码无错误。已查看第二个交界之后的 PNG，确认实际内容可见。

专项入口：先启动 `FrameTrail.Capture.Benchmark.exe --surface 180 --size 640 360`，再运行 `FrameTrail.Workspace.Smoke.exe --checkpoint-timeline <新的输出目录>`。此模式只采集本项目创建的动画测试窗口，关闭系统音、麦克风、摄像头和指针，保留产品默认的 60 秒检查点；约 125 秒后验证两次文件切换、编辑器时间线、原生预览与跨文件导出。测试完成后退出本次创建的动画窗口。

证据目录：`artifacts/validation/checkpoint-timeline/`。专项逻辑回归为 `checkpoint-timeline.trx`，完整默认测试为 `artifacts/test-results/*.trx`。实录为 `real-125s/continuous.frametrailproject`，专项与独立结果为 `real-125s/result.json`、`real-125s/independent-verification.json`，两处交界共 4 张预览为 `real-125s/boundary-*.png`，成片为 `real-125s/cross-checkpoint.mp4`。测试动画进程已退出。

## 边界

本修复不迁移已有项目，不改变主动暂停的编辑段落语义；两分钟本机验证不替代 30 分钟音画同步、多显示器 / DPI、设备故障或 Android USB 真机矩阵。
