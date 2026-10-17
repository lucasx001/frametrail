# 0005 多轨工作区实现与验证

> 历史执行快照。后续集成/修复见 [capture-continuity-performance.md](capture-continuity-performance.md)；当前任务状态以 [progress.md](../progress.md) 和 [待办](../todo.md) 为准。本页未勾选项仅反映记录当日范围。


日期：2026-10-14。实现范围为录制会话、摄像头/光标原生桥接、项目工作区和恢复入口。主 agent 负责统一 review、根构建集成与阶段状态；本文区分实际验证和仍待验收事项。

2026-10-15 补充：摄像头拍摄、持续源 checkpoint 与产品联合短测已通过，本页下方“未拍摄 / 独占重启间隙”描述保留为历史边界；当前证据及未完成项以 [连续性与性能记录](capture-continuity-performance.md) 为准。29.97 FPS、外设中断和长时音画同步仍待验收。

## 实现

- `src/FrameTrail.Workspace/RecordingCoordinator.cs`：固定 RecordingPlan / RunId、单一异步状态机、独立分段、共同屏幕首帧 QPC、暂停/恢复、标记、故障提示。录制暂停时完整结束各轨，恢复创建新分段；暂停期间不创建源时长或标记。
- `NativeRecordingDriver.cs`：屏幕、系统音/进程树、麦克风、摄像头、光标的事务启动与失败回滚。辅助来源中断通过状态提示，结束时仅将已 finalize 且可读素材纳入清单。
- 周期 checkpoint 默认 60 秒。无摄像头时先启动新屏幕/音频/光标分段，以新屏幕 epoch 为旧分段逻辑结束边界；旧文件中额外编码的尾帧不进入项目引用。若双会话资源不可用，保留当前会话、提示周期 checkpoint 已暂停，用户可手动暂停完成 checkpoint。
- 摄像头设备可能独占，当前启用摄像头时周期 checkpoint 使用结束/重新打开设备。实际 QPC 间隙记录到 `recovery/diagnostics.jsonl` 和用户提示；没有将此路径声称为无缝连续录制。
- `ProjectWorkspace.cs`：单写文件锁、SessionEpoch + 单调 Revision、单读者保存队列、不可变序列化快照、同卷临时文件 flush/replace、`project.previous.json`、关闭/移动/删除屏障。失败 revision 可重试，过期 epoch/revision 不落盘，关闭后不会重建目录。
- 打开/恢复拒绝路径逃逸、设备保留名、链接/目录联接、未知项目字段和未来 schema。对媒体检查 MP4 box 完整性/WAV 长度，再通过 Media Foundation 读取媒体时长并真正解码首样本。恢复优先读取最近可用 checkpoint；损坏清单或最新版媒体可回退前一有效快照，未完成末段保留在磁盘并明确列为排除项。
- `native/FrameTrail.Engine/recording`：Media Foundation 摄像头枚举/格式/独立 H.264 轨道，保留有理 FPS（包括 30000/1001），源画面不做镜像；四帧有界队列、摄像头格式变化/EOS/错误处理、原生媒体 probe。
- 原生光标线程采样 Win32 位置/形状，低级鼠标 hook 仅记录鼠标按钮事件，不监听键盘文本；有界事件队列、归一化坐标、物理几何快照和 PNG 光标素材。事件的默认形状明确为 arrow。几何每个 timer tick 更新，hook 复用当前几何快照；磁盘队列不以处理时位置覆盖过去事件。
- WinUI：来源与物理区域输入、音频范围/设备、麦克风电平、摄像头格式、工程路径、开始/暂停/继续/停止/标记、打开/恢复入口、备忘录、capture exclusion 和可重设全局停止/标记快捷键。辅助设备枚举失败不会阻止纯屏幕录制。`--smoke-test` 保留。

## 自动测试和构建

- Workspace 单元测试最终 **21/21 通过**（`artifacts/test-results/FrameTrail.Workspace.Tests.trx`）。覆盖修订排序、上一版保留、同 revision 失败重试、epoch/身份隔离、锁、路径拒绝、移动/删除屏障、损坏 MP4/解码失败、未来 schema、恢复清单回退、损坏最新版媒体回退、未完成末段排除和录制状态转换。
- 持久化单测通过显式注入 validator/media probe 隔离原生硬件；测试用最小 MP4 box 只是单元测试文件，不是录像验收证据。真实 smoke 使用 C++ 权威项目校验与 Media Foundation 解码。
- 最终 WinUI App Debug 构建曾实际通过，0 warning / 0 error；主 agent 继续执行统一 Debug / Release / locked restore 验证。最终测试数量以 `artifacts/test-results` 和主 review 记录为准。
- 独立硬件 harness：`tests/FrameTrail.Workspace.Smoke`。调用 `FrameTrail.Workspace.Smoke.exe <new.frametrailproject> --checkpoint` 录制 7 秒、暂停 1 秒、恢复 3 秒、停止并重新打开。默认摄像头关闭，只有显式 `--camera` 才启用。恢复模式为 `<existing.frametrailproject> --recover`。

## 真实硬件编排验收（主 agent 执行，本文核对共享证据）

1. `artifacts/validation/workspace-review.frametrailproject.result.json`：`success=true`。实际显示器区域 640×360，系统音 + 麦克风 + 独立光标，周期分段和一次暂停/恢复产生 **3 个主画面分段、12 个媒体引用、2 个标记**。源时长 **10.0953812 秒**；停止后完成原生校验、媒体解码检查并成功重开，warnings 为空。未启用摄像头。
2. `artifacts/validation/crash-review.frametrailproject.recovery.result.json`：主 agent 在首 checkpoint 写入后终止本次启动的录制进程，随后运行真实恢复。`success=true / recovered=true / reopened=true`，恢复 **1 个主段、4 个媒体引用、5.4668519 秒**；未完成的第二段屏幕、音频和光标均明确排除。首次 checkpoint 尚无 previous 文件，恢复警告如实记录该事实。
3. 这两项证明短时联合录制、逻辑分段、暂停/恢复、完整项目重开和已完成分段崩溃恢复可运行；不证明长时音画误差、所有设备断开行为或摄像头质量达标。

## 仍需实机验收

- [ ] 摄像头真实视频轨与屏幕/声音同步、29.97 FPS 设备、摄像头中断及独占设备 checkpoint 间隙。本机可枚举 USB UVC 摄像头，未将枚举成功当作实际拍摄通过。
- [ ] 摄像头第一帧以 Media Foundation sample 时间差和首接收 QPC 建立映射；设备管线延迟和抖动需要闪光/提示音素材测量，当前不承诺 <=50ms。
- [ ] 录制窗口、备忘录的 capture exclusion 在显示器/窗口/区域模式下逐一视觉比对。API 调用存在不等于已实测排除。
- [ ] 跨显示器移动窗口、负坐标和不同 DPI 的光标形状/点击位置精确比对。
- [ ] 默认系统声音变更、麦克风/摄像头拔插、主窗口关闭、锁屏和 GPU 丢失的组合验收。
- [ ] 快捷键冲突与重新设定、键盘导航、真实 UI 完整操作流程。
- [ ] 30 分钟联合录制、音画首尾偏差、周期双会话编码器资源压力和内存/句柄稳定性。
- [ ] 摄像头实时预览/预览镜像需后续共享渲染画布接入；当前 UI 提供录制设备和格式选择，原始摄像头媒体不镜像。

技术依据：[Media Foundation 设备采集](https://learn.microsoft.com/en-us/windows/win32/medfound/audio-video-capture-in-media-foundation)、[MSLLHOOKSTRUCT 坐标](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct)、[SetWindowDisplayAffinity](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)。
