# M2 — 编辑、预览与导出验证

日期：2026-10-15。范围：0006–0008。实现闭环已接通；任务保持“待验收”，保留人工与硬件矩阵，不替代 M1 验收。

## 实现

- `FrameTrail.Media` 原生 DLL：MF/NV12 解码、D3D11 色彩转换、Direct2D 分层合成，预览和导出共用 `Renderer::render`。媒体像素不跨 C ABI。
- WinUI `EditorWindow` / `PreviewSurface` / `EditorTimeline`：原生 swap chain、播放/暂停/seek/逐帧、画布缩放、片段选择、吸附、裁剪/重排拖动、后台缩略图/波形、画布与摄像头布局。
- Core 编辑命令返回经校验的新快照。C# `EditorSession` 负责事务、撤销/重做、保存与导出锁；保留原有单写租约和保存屏障。撤销恢复内容并使用更高 revision。
- 系统音与麦克风按统一时间线混合，支持全局/片段音量和静音；自行实现有界流式 WSOLA 保持音调，不新增第三方音频依赖。
- MP4 为 H.264/AAC，M4A 为 AAC；源素材副本附带可重开工程。临时文件写在目标旁，finalize 和媒体校验成功后才替换；不允许覆盖工程内的源文件。

## 本轮已通过

| 范围 | 证据 |
|---|---|
| Release 构建 | App 0 警告、0 错误；原生新模块成功构建 |
| 默认回归 | 4/4 原生测试组，101/101 托管测试（App 3、Capture 6、Domain 68、Workspace 24）；设备测试未计入 |
| 剪辑闭环 | 3 秒素材分割、裁剪、重排、2 倍速后得到 2.25 秒时间线；撤销/重做、取消事务、自动保存、重开内容一致 |
| 保存失败 | 锁定 project.json 后写入失败，内存修订/历史保留；解锁后同修订重试成功 |
| 定时输出 | 30 FPS 输出 68 帧，60 FPS 输出 135 帧；视频流均 2.250000 秒，未按实时计时器漏帧 |
| AAC 尾部 | PCM 输入精确为 108000 个立体声采样帧；AAC 流 2.261312 秒，比时间线多约 11.3 ms，处于一个 1024 样本 AAC 包的舍入范围内 |
| 共同渲染 | 相同首帧预览与 H.264：RGB MAE 2.248/255、PSNR 30.81 dB；阈值为 MAE ≤3.5、PSNR ≥29 dB |
| 真实录屏工程 | 此前 WGC 录制的两个静态分段可预览首尾，并导出 79 帧 MP4；修复单帧保持与容器时长取整造成的尾部 seek 拒绝，真正缺失的源时段仍明确报错 |
| 源 seek | 请求第 60 帧时，烧录帧号区域在 58–62 候选中与 60 最接近；修复 MF 正 composition origin 引起的两帧偏移，并验证尾部 seek |
| SDR 转换 | 显式 NV12→BGRA 矩阵/范围；FrameTrail 源契约为 BT.709，未知矩阵使用该契约，不按画面高度猜 BT.601。GPU 与软件色度重建容差单独记录 |
| 音频混合 | 440 Hz 系统轨与 880 Hz 麦克风轨均存在，功率比约 1；片段静音为零，片段增益 2 的 RMS 为基础增益的约 2 倍 |
| 保持音调 | 0.25/0.5/1/1.5/2/4 倍均检测到 440 Hz，输出样本数分别 576000/288000/144000/96000/72000/36000 |
| 编码能力短测 | 1080p/1440p/4K × 30/60 FPS，六组均编码、解码并核对精确帧数；每组仅 0.2 秒，不代表长时吞吐 |
| 文件安全 | 目标被占用和编码中途取消均保留原有目标，清理本任务临时文件；素材副本可重开 |
| WinUI | 窗口内播放、暂停、seek generation、分割/变速/重排/历史与保存通过；0.75 秒播放提交 35 帧，约 47 FPS，尚未达到 60 FPS 目标 |
| 密集时间线 | 10000 片段在低缩放下绘制 113 个可见/聚合元素，构建约 25.5 ms；图像/波形工作不在 UI 线程执行 |
| 关闭生命周期 | 取消缓存任务、分离 swap chain、销毁 reader/renderer、释放租约后退出；验证进程正常结束，不残留隐藏编辑器 |

原生 1920×1080 解码/合成短测：120 帧约 0.186 秒，约 647 FPS 的非实时吞吐。它包含真实 1080p 源解码，但不是显示器实际呈现帧率；不能用这个数代替窗口约 47 FPS 的结果。

机器为 Windows 11 x64 build 26300，存在 NVIDIA RTX 4060 Laptop GPU 与 AMD Radeon 610M。当前媒体会话报告非 WARP、无 CPU 解码回退；没有由此推断编码器必定硬件加速。

## 证据与复现

完整媒体与独立分析结果在本地忽略目录：

- `artifacts/validation/m2/run-12/m2-validation.json`
- `artifacts/validation/m2/run-12/image-audio-metrics.json`
- 同目录 MP4/M4A、混音/变速 WAV、预览 PNG 和可重开项目
- `artifacts/validation/m2/recorded-source/result.json` 为真实 WGC 素材的接通验证
- `artifacts/validation/m2/ui-04/editor-validation.json`、`lifecycle.log`
- `ui-04/editor-controls.png` 为 WinUI 控件导图；`preview.png` 为共同 GPU 合成器导图

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory C:\Users\lucas\Desktop\project\frametrail\artifacts\native-release -LockedRestore
.\scripts\validate-m2.cmd -Matrix
.\scripts\run.cmd -Configuration Release -ProjectPath "C:\Videos\Demo.frametrailproject"
```

媒体脚本使用本机 ffmpeg/ffprobe 创建合成素材并独立解码核对，Python 仅使用标准库；这些工具不是产品依赖。WinUI 显式验证入口为 `FrameTrail.App.exe --editor <一次性工程副本> --m2-self-test <结果目录>`，会修改该副本后退出，请勿指向需保留的原工程。

SwapChainPanel 不包含在 RenderTargetBitmap 控件导图中，因此分别保存真实合成画面和 UI 图，不将黑色占位当作实际预览缺失，也不将导图当作 OS 鼠标命中证据。

补充桌面操作：Windows Computer Use 已实际看到 swap chain 画面与编辑器，点击播放后播放头由 0 推进至 2.249 秒并在片尾暂停；导出设置面板可以打开并取消。完整拖动和系统文件选择器导出仍未据此宣称通过。

## 待完成的验收与限制

- 原生鼠标完整拖动/关闭行为、跨显示器负坐标与 125%/150%/200% DPI、GPU device lost 注入、长时间反复切换项目的资源曲线。
- 复杂人声和音乐在全部变速档位的人工听测；FFT/采样数通过不等于听感通过。
- 磁盘不足、驱动/编码器故障、长时 4K60 与多 GPU 性能矩阵。错误/取消分支已实现，未实际执行的故障不标记通过。
- 编码输入目前使用有界 CPU readback；尚未完成整条硬件编码/零拷贝确认。PCM 混音缓存受 RIFF 4 GiB 上限约束，超限明确报错。
- M3 高级效果尚未渲染。包络完整保存、保持源时间锚定，带高级视觉效果的 MP4 导出明确拒绝；纯音频与素材副本仍可使用。
- M1 摄像头连续分段间隙、硬件音画同步及先前 review 的保存/恢复问题不因本次 M2 验证自动关闭。没有提交、推送或发布。

技术依据：[MF Source Reader](https://learn.microsoft.com/en-us/windows/win32/medfound/processing-media-data-with-the-source-reader)、[D3D11 解码设备管理](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation)、[Sink Writer finalize](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-imfsinkwriter-finalize)。
