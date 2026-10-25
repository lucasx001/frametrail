# 0031 主 agent 集成审阅与验证

日期：2026-10-25（Asia/Singapore）。工作区：`C:/Users/lucas/.codex/worktrees/openscreen-review-integration/frametrail`。分支：`codex/openscreen-review-fixes`。基线：`ffb401be0bdcaabfc66ee2408eb51d3ca84abf25`；最终代码构建修订：`d878b5f`，后续文档独立提交。

## 独立任务与主审阅

用户确认仅执行原报告中的五项实现问题，每项一个 sub-agent，在独立 worktree 工作；功能建议未纳入本轮。

| 任务 | 子 agent 原提交 | 集成提交 | 审阅结果 |
|---|---|---|---|
| R1 光标连续性 | c1ff8b8 | 559cb1e | 偏移后按源时间选轨；有界历史保留文件形状来源，逻辑剪辑边界隔离；共享预览/导出 |
| R2 增量规划 | cc40885 | d878b5f | 素材先保存、锁外串行计算、runId/workspace/修订合并；人工运镜优先；媒体身份缓存仅在当前租约使用 |
| R3 流式导出 | 43d4b34 | d8a94ac | 连续 WSOLA 保留状态，有界 PCM 包与视频交错；取消/失败不替换目标，GPU 回退重新初始化局部工作 |
| R4 控制组件 | 7a7763e | dddf1af | 播放、手势、素材任务、导出生命周期有明确所有者；进度消费者失败时先排空原生导出 |
| R5 测试/规格 | 10700a5 | 0f76247 | 三链实际窗口方法集成与独立解码可复跑；CPU、应用方法、实际输入声明分别记录 |

集成唯一内容冲突位于 `EditorWindow.Assets.cs`：R5 的只读测试观察口仍引用旧 worker；保留 R4 的 `!assetCoordinator.AudioCompleted` 实现。

主 agent 另发现取消缩略图工作可在 `BeginThumbnail` 后、每帧 finally 前退出，造成 `inFlight` 残留，替换任务跳过同一帧。提交 `e9188f0` 在排空旧任务后重置标记。新增回归先在未修复实现中失败（期望 `[42]`，实际 `[]`），修复后通过；最终完整 App 回归包含该用例。

R2 规划器的极密集点击规则、旧 API 兼容路径、焦点拆分/裁边/复制元数据已核对并同步 schema。新规划器的 key 不晚于对应点击；旧工程不自动重算。保留整体工程尺寸限制，没有将合成 180 分钟基准当作真实长录制验收。

## 最终组合验证

下列命令全部在主集成工作区执行，使用同一套最终代码；工具链显式指定原仓库 `.tools/toolchain`。原始结果保存在本工作区忽略的 `artifacts/` 内。

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain -LockedRestore
python scripts/verify-cursor-continuity.py --native-bin artifacts/native-release/bin --output artifacts/validation/integration-cursor
.\scripts\validate-m2.cmd -NativeBuildDirectory "$PWD\artifacts\native-release" -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain -OutputDirectory artifacts/validation/integration-media
.\scripts\validate-workflows.cmd -SkipBuild -NativeBuildDirectory "$PWD\artifacts\native-release" -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain -OutputDirectory artifacts/validation/integration-workflows
```

- Release：0 警告、0 错误；9/9 原生测试组通过。托管 App 58、Capture 6、Domain 111、Workspace 90，共 265 项通过；Audio 硬件类别过滤后没有执行，不计入通过数。
- 光标：正/负偏移预览和正偏移 MP4 均检测到 60 个预期白像素；静止跨检查点 0 个白像素；前一文件点击反馈跨界保留 248 个蓝色像素。子 agent 还对修复前 DLL 运行同样脚本，前三项按预期失败。
- 流式导出：六档 0.25–4x 的连续 mixer 参考与 AAC 相关系数均大于 0.999994，保持 440 Hz；非整秒多速率的 M4A/默认 MP4/CPU MP4 相关系数 0.99999079、SNR 47.30 dB。视频 2.25 秒、音频 2.261312 秒，尾部差小于一个 AAC 包；取消、占用目标和无临时 WAV 监听通过。
- 三链窗口方法集成全部通过：受控动画窗口真实 WGC 录制→编辑→导出；效果手势→一次撤销→完全释放重开；原生静音播放与阻塞峰值 worker 运行时切换/关闭并重新取得工程租约。
- 工作流输出经 ffprobe 和 ffmpeg 独立检查：H.264 640×360、3.085833 秒、93 帧，AAC 3.093312 秒，视频/音频全片解码成功。录制只包含测试自己的动画窗口，声音轨为生成的静音，不采集用户桌面/麦克风/系统声音。

证据：

- `artifacts/integration-build.log`、`artifacts/test-results/*.trx`、`artifacts/native-release/Testing/Temporary/LastTest.log`。
- `artifacts/validation/integration-cursor/validation.json` 和对应 PNG/MP4。
- `artifacts/validation/integration-media/m2-validation.json`、`image-audio-metrics.json`、对应音视频和 `artifacts/integration-media.log`。
- `artifacts/validation/integration-workflows/workflow-summary.json`、`application/workflow-validation.json`、`export-probe.json` 和成片。
- 独立子任务证据已另存到 `artifacts/subagent-evidence/`，不依赖归档 worktree 中的忽略产物。

## 实际输入的阻断与保留边界

主 agent 用最终 WinUI exe 打开一次性工程副本，并经 Computer Use 定位唯一测试窗口。激活失败后重新获取状态：控件树可读，但截图显示 Windows 锁屏。因此没有注入鼠标/键盘，也没有把可访问树当作实际命中验收。已请求用户解锁，记录为 `actualInputVerified=false`、`blocked-desktop-locked`。

状态文件：`artifacts/validation/interactive/input-validation.json`；可继续检查的工程：`artifacts/validation/interactive/interactive.frametrailproject`。该实例与原仓库用户应用分开，未修改用户工程。

等待解锁后应执行真实拖动→Ctrl+Z→关闭重开、播放与项目切换/关闭，并单独保存结果。三链应用方法集成的报告保持原 `false` 标志，不修改成实际输入已通过。

跨 GPU/DPI、多屏、真实外设/手机、30 分钟联合音画同步、完整多小时导出听测、磁盘不足/设备丢失及干净安装升级未执行，继续遵循 0010–0013 的扩展验收。

## 文档同步

主 agent 更新 0031 任务页、progress/todo、spec/schema、0016/0024、双语使用指南与版本说明，并在原审阅报告增加本轮实施状态。实现、独立子任务证据、主组合回归和未完成的实际输入/硬件验收分别保留；没有新增功能列表中的箭头、字幕、GIF 等承诺。

所有改动保留在独立集成分支，本轮未 push、发布或合并到原仓库正在使用的 `codex/project-card-delete` 分支。子任务工作区归档工具返回“Another active task still uses this worktree”，因此保留这些受占用的工作区，没有绕过工具删除；验证证据已复制到主集成目录。
