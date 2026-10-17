# M0/M1 主 agent 统一审阅与验证

> 历史执行快照。后续集成/修复见 [capture-continuity-performance.md](capture-continuity-performance.md)；当前任务状态以 [progress.md](../progress.md) 和 [待办](../todo.md) 为准。本页未勾选项仅反映记录当日范围。


日期：2026-10-14。工作目录：`C:\Users\lucas\Desktop\project\frametrail`。

## 范围与结论

0001–0005 各由一个 subagent 实现。主 agent 审阅入口、ABI、资源生命周期、时间/坐标、持久化与恢复调用链，统一集成并验证。M0 本地验收通过；M1 录制原型已实现且核心流程实测通过，扩展硬件/长期同步与性能仍待验收。

## 主 agent 发现并推动修复的问题

1. 机器缺少/存在不匹配的动态 C++ 运行库：WinUI Loaded 但 DLL 加载失败，原生 mutex 路径发生崩溃。统一改用静态 CRT，保持所有权不跨 DLL，重编后窗口和 ABI 测试通过。
2. 捕获回调按上一帧间隔限速会放大时间抖动：改用会话时间 bucket，统计原始回调、实际写帧、队列丢帧和限速丢弃。
3. 多轨暂停边界不应各取当前时间：增加 raw QPC 边界 API，源轨共享首屏 epoch 和同一暂停/停止边界。
4. 保存先提升 revision、失败后同修订重试被拒绝：回退失败的最新预约修订，保留新修订顺序约束，并补故障重试用例。
5. 损坏 recovery checkpoint 会阻断其他候选回退：改为记录警告并继续 project/previous 候选；MP4/WAV 先做结构检查并通过 MF 实际解码探测。
6. 摄像头 MF/COM 运行时先于 reader/source 清理，以及回调和 reader.Reset 竞争：调整运行时范围与回调 reader 引用；帧率协议保留有理数，避免 29.97 FPS 被截成 29。
7. 周期性 stop/restart 主录屏会漏掉切换期间内容：无摄像头路径改成重叠启动下一段，以新首帧裁定旧媒体逻辑尾部。摄像头路径仍有明确记录的重启间隙，不能称为无缝长录制。
8. 辅助设备枚举失败会让主来源一起不可用：UI 分别处理声音和摄像头错误，保留纯屏幕操作。
9. CI 不应依赖真实音频设备：托管硬件测试添加 Category 标记，默认排除；各测试项目使用独立 TRX 文件，锁模式覆盖测试依赖。录音和构建目录加入忽略规则。

## 可重复构建和测试

```powershell
.\scripts\build.ps1 -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\test.ps1 -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -IncludeHardware
```

- 本地 Release 锁定依赖构建成功，原生 4/4 测试组通过。
- 默认 CPU 托管测试 92 项：App 3、Capture 6、Domain 62、Workspace 21。
- 最终 Release 启用硬件测试的统一运行共 96/96 通过（含真实音频 4 项），0 失败；结果见 `artifacts/validation/final-managed-hardware.log` 与各项目 TRX。原生 Core 组含 25,370 次断言/检查；不可把它们当成 25,370 个独立测试用例。
- 最后增加摄像头首个有效样本的 10 秒启动屏障，未收到帧则按失败回滚；该分支已编译但仍需摄像头实机验证。原生 Recording 最终增量构建无警告。
- 最终 Release App 构建 0 警告、0 错误。真实 WinUI 窗口完成 Loaded、引擎版本读取、100 次会话取消/释放、正常关闭，exit 0；结果见本地 `artifacts/validation/winui-final-release-smoke.json`。这不是视觉布局或完整按钮交互验收。
- 构建日志、TRX 和原始媒体保存在忽略的 artifacts/build 目录；未执行云端 CI 或安装包验收。

## 真实录制与恢复

- 10 分钟 1080p 区域录制生成 604.425250 秒 H.264 MP4，26,680 帧；完整解码无错误。实际平均约 44.14 FPS，未达到 60 FPS；详细环境、计数和限制见 [0003 记录](0003-capture-review.md)。
- 系统音、当前进程树音频、麦克风都已真实短录，精确暂停样本计数与 QPC 一致；不等于 30 分钟联合音画同步通过，见 [0004 记录](0004-audio-validation.md)。
- `FrameTrail.Workspace.Smoke <new.frametrailproject> --checkpoint` 实际执行：系统音+麦克风+光标、5 秒周期检查点、添加标记、暂停 1 秒、恢复、停止、重开并通过 C++ 权威校验。
- 主 agent 的结果：源时长 100,953,812 ticks（约 10.095 秒）、3 主片段、12 媒体引用、2 标记、无 warnings，独立声音和光标随主段保存。结果：`artifacts/validation/workspace-review.frametrailproject.result.json`。
- 实际崩溃验证：主 agent 启动新录制，观察首个完整 project.json 与下一段 pending 清单后终止该进程；不是仅修改 manifest 模拟崩溃。运行 `<project> --recover`，恢复 54,668,519 ticks、1 个主段及声音/光标共 4 引用，排除第二段未完成素材，并再次打开成功。证据：`crash-kill-evidence.json`、`crash-recording.log`、`crash-review.frametrailproject.recovery.result.json`。

## 尚未通过的验收

- 摄像头实录及断开恢复：本机能枚举 USB2.0 HD UVC WebCam，但未在主 agent 验收中拍摄；有理帧率、格式协商、帧到达延迟仍需真实硬件验证。
- 30 分钟闪光/提示音联合测量、音画偏差 <=50ms、不同音频采样率与多声道外设。
- 125%/150%/200% 与负坐标多屏实机测试、HDR 状态切换、显示器热插拔、GPU 故障和磁盘不足注入。
- 摄像头周期分段的无缝性、4K60、60 FPS 性能目标及多 GPU/稳定版 Windows 矩阵。
- 控制窗口/备忘录的真实像素排除和快捷键冲突交互、摄像头实时预览、区域拖框选择。

以上项不因代码存在或模拟测试通过而勾选。后续进度以 [progress.md](../progress.md) 与各任务验收清单为准。
