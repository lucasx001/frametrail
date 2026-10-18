# P1 编辑、声音与摄像头验证

日期：2026-10-18。分支：`codex/p1-editor-recording`，基于 `f74e3d0`。

当前检出原有录制检查点/时间线修改被保留；本轮新增 0022–0024，并补充本机可靠性检查。没有发布版本或变更外部账户。

## 构建与默认检查

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
```

本轮完整构建与默认测试结果在 `artifacts/p1-build-*.log`；最终结果以本文件下方的最终复验记录为准。新增投影逆变换/旋转缩放、退化平面、缓存清理保护，以及原生 RIFF/RF64/共享投影验证。

`artifacts/p1-build-final-03.log`：Release 0 警告/0 错误，7 组原生测试通过，220 项默认托管测试通过（App 38、Capture 6、Domain 111、Workspace 65）。硬件测试未混入默认计数。

## 媒体与摄像头

```powershell
$env:PATH = "$PWD\artifacts\native-release\bin;$env:PATH"
.\.tools\toolchain\dotnet\dotnet.exe run --project tests/FrameTrail.Workspace.Smoke -c Release -- --p1-media <测试工程> <新输出目录> --camera
```

- `artifacts/validation/p1/media-04/p1-media.json`：真实声音输出、seek、波形与兼容 PCM 准备通过。
- 30/60/120 分钟逻辑工程使用连续源时间的重复测试媒体引用，分别 600/1200/2400 个片段。不是实际录制这么长的素材。播放到前 200ms 的时间约 395/391/403ms；进程工作集约 69/81/107 MB（包括工程数量增长和运行时，不是播放缓冲大小）。
- 本机 USB2.0 HD UVC WebCam、640×480、30 FPS，取得非空 BGRA 帧；预览/录制 source identity 相同，停止文件录制后仍有新预览帧。实际文件位于该输出目录。
- `artifacts/validation/p1/camera-ui/camera-ui.json`：真实 WinUI 预览窗口显示至少 3 帧、镜像开关来回切换及窗口释放通过。该入口为 `--p1-camera-ui-self-test <新目录>`，不修改系统权限。
- `artifacts/validation/p1/m2-regression/m2-validation.json` 及独立 Python 结果：MP4/M4A、音量/静音、0.25/0.5/1/1.5/2/4 倍速保调、取消与目标占用保护通过。六种速度主频均约 440Hz。

## 应用与交互

- `--editor <可丢弃工程> --p1-self-test <新目录>`：投影选择、对象移动、撤销重做、取消、裁切保存、全屏与独立渲染对照。
- 第一轮像素对照误用了不同合成分辨率，测试已统一 4K 预览质量；第二轮通过。
- 扩展轮使用已有裁切工程时，测试初始角点假设不成立；现在先清除测试裁切。没有将失败轮记为功能通过。
- Computer Use 实际打开编辑器，点击裁切并拖动右下角；JSON 裁切范围改变，界面恢复“已保存”。
- 修复裁切中撤销后显示路径：统一显示完整原始画面与命中坐标；裁切模式暂时隐藏运镜、样机和摄像头，不改写这些工程设置。
- 最终扩展结果：`artifacts/validation/p1/ui-final/p1-validation.json` 的 7 类应用检查全部通过。`polish-final/editor-polish-validation.json` 同时验证了故意阻塞波形准备时的有声/静音播放，以及音量修改、撤销复用、连续胶片、光标与预设回归。
- 音频停止在后台序列化，播放请求用 generation 丢弃过期结果；暂停后恢复对象选择层。缓存清理还保护工程清单引用的素材，即使自制工程把源文件放入缓存命名范围，也不会删除。

## 可靠性

- `artifacts/validation/p1/recovery/result.json`：实际终止测试录制进程，恢复 1 个完成片段并保留 1 个未完成文件，再次打开成功。只录制自建动画窗口。
- 30 分钟动态窗口录制入口：`scripts/validate-m4.cmd -DurationSeconds 1800`。`artifacts/validation/p1/long-capture/result.json`、`summary.json`：1799.99 秒，1920×1080 H.264 硬件编码，实际 59.0409 FPS；写入与 FFprobe 独立解码均为 106273 帧，FFmpeg 全片解码通过，队列丢帧 0、水位峰值 1。
- 1772 个资源采样：前/后 30 个样本的私有内存中位数为 168.70/175.33 MiB，峰值 175.33 MiB；工作集峰值 91.06 MiB。句柄全程 931–983，最后 5 分钟 957–963。记录包含同时构建、预览和导出的开发机负载；不推断其他 GPU 或更长时长的结果。
- 纯视频长测不代表系统音/麦克风/摄像头 ≤50ms 联合同步通过。

## 保留待验收

Android 物理手机/ROM/驱动/内部声音、真实多轨长时同步、跨 DPI/显卡/外设、锁屏/睡眠/GPU 丢失和磁盘不足注入，继续留在原任务清单。干净 Windows、跨版本安装升级、Store 身份/WACK/认证需要相应环境和账户；本轮没有替代这些验收证据。

## 分发补充

最新本地候选：`artifacts/packages/p1-final-04/`，597 文件及 ZIP/MSIX 哈希校验、脱离开发 PATH/运行时的应用启动与 100 次引擎生命周期检查通过。日志：`artifacts/p1-package-final-04.log`、`artifacts/p1-package-final-04-verify.log`。前一轮便携载荷也直接运行了完整画布/播放/保存验证（`artifacts/validation/p1/packaged-ui/p1-validation.json`）。本轮未安装或发布该候选。

实时核查 v0.0.1 已于 2026-10-17 18:48:52（UTC+8）公开。已下载全部 7 个附件并验证哈希，关闭 0019 中对应的过期待办；证据见 `public-release-v0.0.1/download-validation.json` 与 [发布验证](github-releases.md)。干净机器/升级/Store 验收仍保留。
