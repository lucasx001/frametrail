# R5 测试层级与规格对齐验证

日期：2026-10-25（Asia/Singapore）。基线 `ffb401be0bdcaabfc66ee2408eb51d3ca84abf25`；独立工作区 `r5-test-spec-alignment`。本任务只增加验收入口与修正文档，没有增加产品功能。

## 实现

- 新增 `scripts/validate-workflows.cmd/.ps1`，在登录桌面上构建并启动实际 WinUI 应用的显式 `--workflow-self-test` 入口。每次使用新目录，保留失败轮和成功轮。
- 录制仅捕获测试创建的动画 WinUI 窗口；不录桌面，不启用系统音/麦克风/摄像头。录制后在测试工程内添加合成静音轨，覆盖原生音频播放和峰值准备的释放路径。
- 三链共用生产录制协调器、编辑事务、CanvasOverlay 手势、撤销、保存、项目切换、关闭和原生渲染/导出。手势由应用方法调用，不声称注入 OS 鼠标。
- 第三链使用现有可取消的音频准备屏障确定性保持峰值 worker 未完成，同时启动原生播放；切换和关闭后重新打开旧工程租约，超时或未释放都失败。
- 应用结果和外层汇总均固定 `actualInputVerified=false`；ffprobe 检查导出轨道、尺寸/时长，ffmpeg 独立解码视频和音频。
- `docs/testing.md` 区分 CPU/契约测试、窗口方法集成、独立媒体检查、实际输入和扩展矩阵；README 和 spec 不再把点击反馈、静止隐藏、时间偏移列为未实现，也不再暗示编辑器有全局“根据点击生成”按钮。

## 本机执行

```powershell
.\scripts\validate-workflows.cmd `
  -NativeBuildDirectory C:\Users\lucas\.codex\worktrees\openscreen-review-integration\frametrail\artifacts\native-release `
  -ToolchainRoot C:\Users\lucas\Desktop\project\frametrail\.tools\toolchain `
  -OutputDirectory "$PWD\artifacts\validation\workflows\r5-02"
```

结果：WinUI Release 构建 0 警告、0 错误；三条应用方法工作流全部通过，导出包含 640×360 视频和 AAC 静音轨，ffmpeg 全片独立解码成功。

证据（相对于 R5 工作区）：

- `artifacts/r5-workflow-02.log`：构建和脚本结果。
- `artifacts/validation/workflows/r5-02/workflow-summary.json`：`success=true`、`chainCount=3`、`actualInputVerified=false`。
- `artifacts/validation/workflows/r5-02/application/workflow-validation.json`：三链各自的来源/结果、捕获范围与实际输入待验收说明。
- `artifacts/validation/workflows/r5-02/export-probe.json`：独立导出轨道信息。
- `application/recorded-preview.png`、`dragged-mask.png`、`reopened-after-undo.png`：原生渲染结果。
- `application/recorded-edited.mp4`：真实受控窗口录制后分割导出的成片。

较早 `r5-01` 只完成前两链，在复制工程时因尚有检查器输入事务而失败，未计入通过。验收入口随后增加已有的视觉/检查器保存屏障，再进入工程复制；这属于测试调用顺序修正。失败证据保留，没有覆盖为成功。

## 给主 agent 的真实输入 fixture

测试结束后窗口全部关闭、工程租约已释放。可先复制下列目录，再用最终集成的 exe 打开副本：

```text
C:\Users\lucas\.codex\worktrees\r5-test-spec-alignment\frametrail\artifacts\validation\workflows\r5-02\application\projects\workflow-take.frametrailproject
```

```powershell
<最终集成 FrameTrail.App.exe> --inspect-ui --language zh-Hans --editor "<工程副本目录>"
```

fixture 含三段编辑片段、一个遮挡效果和合成静音；约 1 秒处可见矩形遮挡，源坐标 `x=.2, y=.2, width=.25, height=.25`，适合实际鼠标拖动及 Ctrl+Z。已有 AutomationId 包括 `editor.effect.add.mask`、`editor.effect.picker`、`editor.effect.x`、`editor.effect.y`、`editor.effect.start`、`editor.effect.end`。画布手柄应使用最新实际窗口几何观察后拖动。

## 验收边界

本轮没有执行 OS 实际鼠标/键盘输入；由主 agent 在集成构建上另行走查并记录。未执行跨 DPI、多显示器、Narrator、真实外设、长时联合音画同步或安装升级矩阵。既有 CPU/契约测试没有被冒充为窗口验收，本次也没有改写历史验证记录中的未完成项。

集成提示：R4 正在抽离后台任务；`EditorWindow.Assets.cs` 的唯一观察口 `IsWorkflowAudioPending` 在 R5 基线为 `!audioWorker.IsCompleted`，集成时保留 R4 的 `!assetCoordinator.AudioCompleted` 版本。其余 R5 窗口验证代码均是新增 partial，不修改现有编辑方法。
