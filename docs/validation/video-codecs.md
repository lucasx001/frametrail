# H.265 / AV1 编码验证

日期：2026-10-16。环境：Windows 11 build 26300、NVIDIA RTX 4060 Laptop GPU（32.0.16.1692）、AMD Radeon 610M（32.0.21025.1024）。

## 实现范围

- 录制胶囊更多菜单和编辑器导出面板各有 H.264、H.265 / HEVC、AV1 选择。默认 H.264，工程设置使用 `h264/hevc/av1`；录制计划跨分段保存，成片导出也保存选择。
- 使用按格式查询的 Media Foundation MFT 能力，分别报告硬件/软件编码器和解码器。录制需要解码器以保证编辑器能重开，导出可以没有输出格式的解码器。不支持项灰显，不替换用户选择的编码。手机原始流和摄像头辅助轨保留 H.264；最终导出可选三种格式。
- 新增 `ft_capture_start_with_codec` 和 `ft_capture_get_codec_capabilities`，旧启动函数、options/status/capabilities 结构保持 ABI 兼容。Capture 实际 transform 检查与 GPU NV12、有界队列保持；软件录制仍限 1080p30。原生导出 writer 使用工程的 codec，复用已有音频、事务替换和取消路径。

## 本机结果

- `artifacts/codecs-native-tests.log`：7 组原生通过；新增不支持 codec/空指针/失败句柄清理检查，协议非法值覆盖 native/managed 两端。
- `artifacts/codecs-managed-tests.log`：App 15、Capture 6、Domain 86、Workspace 58，共 165 项通过。涵盖新格式的协议往返、native 编辑往返、暂停/续录与重新打开后的格式保存。
- `artifacts/codecs-app-build.log`：Release 应用及依赖构建 0 警告 / 0 错误。验证输出隔离，未覆盖运行中的旧程序。
- `artifacts/codecs-final-build.log`：旧窗口正常关闭后，默认 Release 输出构建通过，0 警告 / 0 错误，并通过 `scripts/run.cmd` 启动新版；默认目录的 capture DLL 与已验证 native DLL 哈希一致。之前复制被旧进程占用的记录保存在 `artifacts/codecs-default-locked-build.log`，未强制结束进程。
- 初始本机探测 H.264/HEVC 各 3 个硬件编码器与 1 个软件编码器，AV1 1 个硬件编码器但无解码器；`media-02` 验证 AV1 录制不可用时仍能把 H.264 工程导出为真正 AV1。
- 用户授权安装微软官方 [AV1 视频扩展](https://apps.microsoft.com/detail/9mvzqvxjbq9v)。WinGet 商店连接失败，改用 `https://get.microsoft.com/installer/download/9MVZQVXJBQ9V?mode=direct` 官方安装器，先验证 Authenticode 为 Microsoft Corporation 且签名有效，再安装。当前用户包版本 2.0.35.0，见 `artifacts/video-codecs/av1-extension-installed.json`。未修改系统代理设置。
- `artifacts/video-codecs/media-03/codec-validation.json`：安装后 H.264、HEVC、AV1 三种实际 WGC 录制、暂停/恢复、重开、首帧与中途 seek、导出全部通过。
- `artifacts/video-codecs/media-04/codec-validation.json` 和 `independent-codec-verification.json`：每种格式录两段 640 × 360，捕获诊断均确认实际硬件 transform；加入真实 PCM 音轨，导出 640 × 360 / 30 FPS 和 1920 × 1080 / 60 FPS，共 6 个 MP4。FFmpeg/FFprobe 独立验证 codec 分别为 h264/hevc/av1、AAC 音轨、完整解码、ceil(duration × fps) 帧数和时长一致；预览/成片首帧 RGB 平均误差 0.995–0.998。
- `artifacts/video-codecs/recorder-ui/ui-validation.json`：实际 WinUI 录制选择、H.264 默认、手机来源禁用桌面编码选择、462 × 56 胶囊、设置面板和真实暂停续录回归通过。
- `artifacts/video-codecs/editor-ui/project-open-validation.json`：导出面板三个选项更新摘要并生成截图，项目切换/保存/删除确认回归通过；`export-codec-hevc.png` 和 `export-codec-av1.png` 已检查。8 组热预览与全新渲染误差均为 0，旧成片回归完整解码通过。

## 复验与边界

```powershell
# 使用新的输出目录与测试 PCM 文件；只创建测试工程。
$env:PATH = "$PWD\artifacts\native\bin;" + $env:PATH
dotnet tests\FrameTrail.Workspace.Smoke\bin\Release\net8.0\FrameTrail.Workspace.Smoke.dll --video-codecs artifacts\video-codecs\new-run artifacts\validation\m2\fixture\media\system.wav
python tests\FrameTrail.Workspace.Smoke\verify_video_codecs.py artifacts\video-codecs\new-run
```

本机能力不代表所有显卡、其他显示器适配器、驱动版本、4K 或长时录制通过；格式与尺寸/帧率组合仍由实际 writer 初始化检查。当前是 SDR 8-bit NV12，没有新增 HDR/10-bit。WinUI 位图不包含 GPU swap chain 视频层，媒体画面由独立 GPU 帧与成片解码比较验证。

参考：[Microsoft HEVC encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-265---hevc-video-encoder)、[Sink Writer](https://learn.microsoft.com/en-us/windows/win32/medfound/using-the-sink-writer)、[MP4 sink](https://learn.microsoft.com/en-us/windows/win32/medfound/mpeg-4-file-sink)。实际 AV1/HEVC MP4 能力以本机测试为准。

## 补充：分辨率、帧率与失败保护矩阵

日期：2026-10-16。本轮只新增测试入口和基准参数，产品编码路径未修改。当前启用一个 2560 × 1440 SDR 显示器；4K 录制测试创建自身的 3840 × 2160 动画窗口，不把它等同于物理 4K 显示器或多个显示器的验证。

- `artifacts/codecs-matrix-build.log`：新增托管矩阵工具构建 0 警告 / 0 错误。`artifacts/codecs-benchmark-build.log`：扩展原生 benchmark 编译/链接通过；使用项目工具链环境初始化，未更改系统配置。
- `artifacts/video-codecs/matrix-01/export-matrix.json`：H.264、HEVC、AV1 各自 1080p/1440p/4K × 30/60，18 个 MP4 + AAC；每个时间线 0.5 秒。每种格式额外注入编码中的取消和文件独占占用，均保留既有目标内容且清理任务临时文件。
- `artifacts/video-codecs/capture-matrix-01/capture-matrix.json`：每种格式 1080p/4K × 30/60，共 12 次实际 WGC 动画窗口捕获，每次 3 秒。全部确认实际硬件 transform；队列丢帧均为 0。FFmpeg 完整解码通过，codec、尺寸、时长、独立读出帧数与原生写入计数一致。
- `artifacts/video-codecs/matrix-01/independent-matrix-verification.json`：18 组成片均完整解码、15/30 帧、0.5 秒和 AAC 时长检查通过；12 个捕获文件在 0.3/1.7 秒采样并缩至 160 × 90，画面差值均大于 1，排除只有计数增长而图像冻结的情形。

| 编码 | 1080p60 实测 FPS | 4K60 实测 FPS | 队列丢帧 |
|---|---:|---:|---:|
| H.264 | 60.16 | 59.66 | 0 |
| H.265 / HEVC | 59.83 | 59.50 | 0 |
| AV1 | 60.00 | 60.00 | 0 |

录制使用真实可变时间戳；表中 FPS 为原生写入帧数除以记录时长，短测初末边界会影响比值。帧率限制主动过滤的回调另计，不记作队列丢帧。以上是本机短测，不代表 30 分钟联合录制、所有 GPU、故障驱动或跨显示器矩阵通过。

复验（每次指定全新目录，先构建 Smoke 与 Capture.Benchmark）：

```powershell
$env:PATH = "$PWD\artifacts\native\bin;" + $env:PATH
dotnet tests\FrameTrail.Workspace.Smoke\bin\Release\net8.0\FrameTrail.Workspace.Smoke.dll --video-codec-matrix artifacts\video-codecs\media-04\h264.frametrailproject artifacts\video-codecs\matrix-new
python tests\FrameTrail.Workspace.Smoke\run_codec_capture_matrix.py artifacts\native\bin\FrameTrail.Capture.Benchmark.exe artifacts\video-codecs\capture-matrix-new
python tests\FrameTrail.Workspace.Smoke\verify_codec_matrix.py artifacts\video-codecs\matrix-new artifacts\video-codecs\capture-matrix-new
```
