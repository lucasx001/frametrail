# R4：编辑器控制职责拆分

日期：2026-10-25。基线 `ffb401be0bdcaabfc66ee2408eb51d3ca84abf25`，独立 worktree `r4-editor-controllers`。对应 Open Screen 对照审阅的 R4。

## 实现

- `EditorTransportController` 拥有播放时钟、帧调度、播放请求版本及音频串行信号量。暂停使在途启动失效；新的播放等待上一轮音频停止；窗口仍负责 CompositionTarget 订阅、图标和画布呈现。
- `EditorEditController` 拥有唯一活动手势及其所有者（时间线、画布、检查器）。不同控件不能提交或取消其他控件的手势；预览继续基于原始事务快照，提交仍进入 `EditorSession` 的保存、撤销路径。窗口中的 `canvasTransaction`、`visualTransaction` 变成派生只读状态，消除重复状态源。
- `EditorAssetCoordinator` 拥有缩略图与峰值两个后台工作的取消、替换、等待屏障。替换操作按任务串行，先排空旧工作再启动新工作；停止同时等待两类任务与音频停止。缩略图需求、去重、在途标记和 128 项请求队列移入协调器，窗口保留实际媒体生成和 UI 发布。
- `ExportJobService` 接收冻结工程并拥有原生导出会话、取消注册、进度轮询、停滞取消和最终释放。`EditorSession` 保留工程保存屏障和 `IsExporting` 排他状态。导出工作可注入测试适配器，无需真实编码器就能验证取消与释放顺序。
- 导出进度消费者同步抛错时，现在先取消并等待原生任务结束，再释放媒体会话，避免原实现中后台导出仍使用已释放会话的风险。

没有改动时间线领域映射、媒体 ABI、渲染算法或产品功能。检查器的 Dispatcher 定时器和控件值读取仍留在窗口；此次提取针对非 UI 生命周期，不以继续拆 partial 作为完成依据。

## 自动检查

工具链来自主仓库 `.tools/toolchain`；只读使用主集成工作区已经构建的原生 DLL，未重新配置或修改该构建目录。

```powershell
$env:PATH = 'C:/Users/lucas/.codex/worktrees/openscreen-review-integration/frametrail/artifacts/native-release/bin;' + $env:PATH
.\scripts\test.cmd -Configuration Release -NativeBuildDirectory C:/Users/lucas/.codex/worktrees/openscreen-review-integration/frametrail/artifacts/native-release -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain -SkipNative -LockedRestore
C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain/dotnet/dotnet.exe build src/FrameTrail.App/FrameTrail.App.csproj -c Release -p:Platform=x64 -p:NativeBuildDirectory=C:/Users/lucas/.codex/worktrees/openscreen-review-integration/frametrail/artifacts/native-release -p:RestoreLockedMode=true
```

- WinUI Release 实际构建：0 警告，0 错误。
- 默认托管回归：App 57、Capture 6、Domain 111、Workspace 78，共 252 项通过。Audio 硬件类别没有默认匹配测试，不计入通过数。新增最后一项并发替换测试后，重新执行全部 App 测试 57/57 通过；其他项目结果来自同一实现的前一轮完整默认检查。
- 新增 8 项控制器测试，覆盖暂停与在途音频启动竞争、旧音频停止屏障、会话替换拒绝启动、停止失败后恢复、工作替换取消排空、同时替换互斥、停止等待播放屏障和当前缩略图需求去重。
- 新增 3 项导出工作测试：预取消不创建会话，中途取消等待原生工作停止后释放，进度消费者失败取消并排空工作。
- 手势所有权测试使用真实 `EditorSession`、原生领域命令与工程写入，确认跨控件提交被拒绝、多个预览只产生一步撤销、取消不保存草稿。

## 实际应用自测

使用本 worktree 构建的 WinUI exe、4 秒合成画面和 440 Hz 双声道音频。通过现有应用自测入口调用生产路径，未操作用户正在运行的原仓库应用。

- `artifacts/validation/r4-editor-polish/ui/editor-polish-validation.json`：7 项通过，包括峰值准备屏障期间的独立缩略图与有声/静音播放、图片编辑不重建音频、增益失效与撤销缓存复用、光标共享预览导出，以及命名预设实际 WinUI 对话框。
- `artifacts/validation/r4-p1/ui/p1-validation.json`：7 项通过，包括 mask/focus/sticker 的投影画布编辑、一步撤销/重做、重新渲染像素一致、旋转/倾斜命中与取消、裁切、有声预览、全屏和保存回读。
- 工作替换串行化之后的最终构建再次执行 polish 自测，结果保存在 `artifacts/validation/r4-editor-polish/ui-final/`。

应用自测直接调用生产方法/控件 peer，不能称为真实鼠标手势验收。主 agent 集成后仍需运行 R5 三链和真实输入走查；跨硬件、DPI、复杂长工程及设备失效仍需对应环境。R1/R2/R3 的媒体修改不在本独立分支的验证范围内，最终组合回归由主 agent 执行。
