# M4 可靠性与开发候选分发记录

日期：2026-10-16。分支：`codex/m4-reliability-distribution`，从最新 `origin/main`（`822c592`）创建。应用、原生引擎及候选包版本 0.4.0，ABI 仍为 1。

结论：M4 的本机可靠性修复、验证工具和候选分发链路已实现。0010/0011 保持待验收；当前没有正式发布结论。候选 MSIX 未签名，未执行安装、购买证书或公开发布。

## 环境与构建

Windows 11 build 26300，AMD Ryzen 9 7940HX（16 核 / 32 线程），NVIDIA RTX 4060 Laptop（32.0.16.1692）与 AMD Radeon 610M（32.0.21025.1024）。现有开发机器有两块不同刷新率的显示器；这不代表跨显示器、DPI 或两块 GPU 已分别验收。

`scripts/build.cmd -Configuration Release -LockedRestore`：0 警告、0 错误；6 组原生测试通过。默认托管共 116 项：App 7、Capture 6、Domain 69、Workspace 34；音频硬件测试默认排除。TRX 在 `artifacts/test-results/`。上述为本地验证结果；云端 GitHub Actions 以对应 PR / 提交的检查状态为准。

## 可靠性与故障回归

| 项目 | 结果与证据 |
|---|---|
| 清单缺失/损坏 + 当前 JSON 损坏 | 回退到可读的 previous 快照并重新打开；两组单测通过 |
| 已完成工程 JSON 损坏 | 进入恢复候选列表，保留清单检查点并恢复；单测通过 |
| 未来 schema / 未知字段 | 拒绝恢复覆盖，原 JSON 字节内容保持；两组单测通过 |
| 真实录制进程终止 | 受控动态窗口 checkpoint 后终止本次子进程，恢复 1 个完整分段，保留 1 个未完成尾段，重开成功 |
| 过期 epoch/revision、写入租约、关闭/移动屏障 | 既有回归通过；不代表所有 OS 关闭时序均已覆盖 |
| 导出取消/目标占用 | M2 真实媒体回归保留原目标并清除本任务临时文件；磁盘耗尽仍未测 |
| 脱敏诊断 | 异常消息、内部异常文本、媒体路径及 token 示例均不进入报告；最多保留 10 份新诊断，旧原始日志不进入导出 |

原始终止恢复证据：`artifacts/validation/m4/process-recovery/result.json` 与 `interrupted.frametrailproject.recovery.result.json`。脚本只终止自己创建的录制/测试画面子进程，保留原始媒体，不终止用户的其他实例。

复现：

```powershell
.\scripts\validate-recovery.cmd
.\scripts\validate-m2.cmd -NativeBuildDirectory artifacts\native -Matrix
```

M2 回归原始证据在 `artifacts/validation/m4/m2-regression/`：真实解码/合成、中文工程路径、剪辑/撤销/重做、保存、混音、0.25–4x 六档音调、MP4/M4A、素材副本、取消/占用，以及 1080p/1440p/4K × 30/60 FPS 六组帧数校验均通过。输出矩阵为短合成素材，不是采集或复杂效果性能验收。

## 视频性能与资源短测

受控 1920×1080 动态窗口，WGC + GPU NV12 + MF 硬件写入，指定 60 FPS，运行 60 秒。未采集桌面、摄像头或麦克风；这项性能测试无音轨。采集与编码报告分开保存，不用输出头帧率代替实际捕获率。

最终验证：实际 59.9941 秒，写入 / 独立解码均为 3,551 帧，约 **59.1891 FPS**，队列丢帧 **0**，队列高水位 **1**；全片 FFmpeg 解码通过。FFmpeg/ffprobe 仅为开发验证工具，未随产品分发。

59 个资源采样点：私有内存首尾约 166.8 / 164.2 MiB，范围约 163.9–168.0 MiB；句柄 917–923，线程 92–93。短测未见单调无界增长，不证明 30 分钟稳定或 GPU 纹理池无泄漏。运行期间同机也进行候选包检查，不能当作隔离负载的性能比较。

原始证据：`artifacts/validation/m4/capture-verified/{environment,capture,probe,result,resources}.json`、`capture.mp4`。早期 `capture-60s` 记录约 59.48 FPS，但 PowerShell 进程退出码读取失败；最终脚本改用直接 Process 对象，以上 `capture-verified` 为完整成功入口。

```powershell
.\scripts\validate-m4.cmd -DurationSeconds 60
# 仅用于长视频采集/资源记录；仍须独立进行 A/V 标记与首尾误差测量
.\scripts\validate-m4.cmd -DurationSeconds 1800
```

## 分发候选与完整工作流

候选目录：`artifacts/packages/m4-0.4.0/`。输出：

- `FrameTrail-0.4.0.0-x64-portable.zip`：自包含便携载荷。
- `FrameTrail-0.4.0.0-x64.msix`：未签名开发候选，正常侧载前需要可信签名。
- `payload-manifest.json`：版本、架构、最低系统、签名状态、源提交 / dirty 状态、532 个文件及 SHA-256。
- `nuget-dependencies.lock.json`、`SHA256SUMS.txt`、`makeappx.log`：依赖版本、分发校验与包结构验证记录。

载荷包含 .NET、Windows App SDK、五个原生 DLL、XBF/PRI、字体、文档及原始许可。首个候选遗漏 publish 的 XBF/PRI，独立启动发生 XAML 解析错误；现已通过 MSBuild 发布目标补齐，并在打包时逐项拒绝缺失资源。PowerShell 5.1 的 XML 编码、SDK 工具选择及 ZIP 路径分隔符也已在实际命令中修正。

`verify-package.cmd` 对本地载荷、MSIX 与便携 ZIP 逐文件验 SHA-256，所有文件一致。`-SmokeTest` 清除子进程开发运行时环境变量、将 PATH 限为 Windows 系统目录；实际 WinUI Loaded、100 次原生会话检查及正常退出通过，运行时、WinUI 和产品模块都来自候选载荷。

`-WorkflowTest` 在同一候选中执行真实区域录制 → 标记 → 暂停/继续 → 停止 → 原生校验重开 → 预览 → 分割 → 撤销/重做 → 保存 → MP4 导出 → Media Foundation 重开。实际产出两段录制和一个标记；工作流通过。UI 图像来自 WinUI RenderTargetBitmap，测试通过应用内部入口，不等同于操作系统鼠标和完整键盘验收。

复现命令：

```powershell
.\scripts\package.cmd -OutputDirectory artifacts\packages\new-candidate
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\new-candidate -WorkflowTest
```

完整记录在候选目录的 `smoke-result.json`、`workflow/recorder/ui-validation.json`、`workflow/editor/editor-validation.json` 与 `recorded-edited.mp4`。该本地候选由当时未提交的工作区生成，manifest 的 `sourceDirty=true` 如实记录构建时状态；后续代码提交与推送状态以 Git / GitHub 记录为准。

本次仅准备未签名 MSIX，未安装包身份。便携载荷工作流不等同于 MSIX 安装工作流。设置的更新按钮只查询，不下载、退出或安装。未提供更新来源时没有默认服务器；可选 HTTPS App Installer 文件与已有证书签名入口仍需真实服务和证书验收。分发设计见 [说明](../distribution.md)。

## 验收矩阵与发布阻断

| 配置 / 场景 | 状态 | 解除条件 |
|---|---|---|
| 当前混合 GPU 开发机，1080p60 受控窗口短录制 | 通过（上述范围） | 长录制另验 |
| 当前机器便携载荷的录制/编辑/导出 | 通过 | 稳定系统干净机另验 |
| Intel、独立 AMD、独立 NVIDIA、跨 GPU / 显示器 / DPI | 未测 | 取得对应机器并分别记录适配器、DPI 和结果 |
| 30 分钟联合音画误差 ≤50ms、长时内存/纹理/句柄 | 未测 | 真实可识别声画标记与首尾误差原始测量，不能用时钟数学测试替代 |
| 4K60 采集、复杂效果 1080p60、HDR | 未测 | 记录独立采集/预览/导出吞吐与降级提示 |
| USB 音频/摄像头断开、窗口关闭、显示器改变、锁屏/睡眠、GPU 丢失 | 未测 | 真实故障注入和有效媒体/工程恢复证据 |
| 磁盘耗尽、更新/安装失败 | 未测 | 隔离测试卷及真实部署失败提示，保护有效文件 |
| 完整键盘、英文、高对比度及多 DPI 焦点 | 未完成 | 逐流程人工/辅助技术验收；当前界面以中文为主 |
| 正式签名、稳定 Windows 11 干净机器安装/升级/卸载 | 未测 | 可信证书/签名服务和独立环境；验证工程及偏好保留策略 |
| 在线 App Installer 检查与服务故障 | 未测 | 真实 HTTPS 来源、可信签名及版本升级矩阵 |

这些阻断在取证前保持未勾选。没有把本机开发候选或短测写成正式可发布版本。
