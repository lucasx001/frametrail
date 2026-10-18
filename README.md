# FrameTrail

面向 Windows 11 的原生录屏与演示视频编辑软件。

> **项目来源说明：FrameTrail 改编自 [DogSC](https://github.com/laogou717/dogsc)，原作者为 [laogou717](https://github.com/laogou717)。**
>
> 本项目以 DogSC 的产品设计、界面布局和编辑领域模型为基础，将其录屏与编辑体验移植到 Windows，采集、编辑、预览和导出采用 Windows 原生技术重写。感谢原作者开源分享；来源、原项目 MIT 许可及相关版权声明见 [第三方声明](THIRD_PARTY_NOTICES.md)。

M0–M3 已形成录制、非破坏性编辑、运镜包装与 MP4/M4A 导出的本机闭环；M4 已提供可靠性修复和开发候选包。长时同步、跨硬件和正式分发验收仍待完成，当前任务以 [进度](docs/progress.md) 和 [待办](docs/todo.md) 为准。

移动设备录屏方向为 [通过 USB 数据线连接 Android 手机](docs/0012-android-usb-recording.md)，M5 的本地实现与自动媒体验证已完成；USB 真机、长时同步与安装矩阵待验收，见 [M5 验证记录](docs/validation/0012-android-usb-validation.md)。

录屏界面已参照 DogSC 重做为悬浮胶囊与输入配置面板，包含来源选择遮罩、区域拖框、录制条、完成卡片和备忘录。实现与验证边界见 [录屏 UI 记录](docs/validation/0005-recorder-ui.md)。

编辑器、六项检查器、通栏时间线、导出与设置界面也已按 DogSC 截图及设计源码重做，支持统一的明暗外观。M3 已接通点击自动运镜、手动缩放/透视、聚焦与遮挡、贴图、开场/摄像头动画、工程内背景和场景预设，见 [M3 验证](docs/validation/m3-motion-effects.md)；参考来源与许可见 [第三方声明](THIRD_PARTY_NOTICES.md)。

P1 体验补充已接入画布对象与裁切框直接编辑、共享摄像头实时预览、无需整片缓存的有声播放，以及 RF64 与缓存管理。实现与验收边界见 [P1 验证](docs/validation/p1-editor-recording.md)。

- [产品规格](docs/spec.md)
- [实现进度与任务顺序](docs/progress.md)
- [当前待办与验收入口](docs/todo.md)
- [媒体缓存、光标、连续缩略图与预设管理](docs/validation/editor-polish.md)
- [工具链与构建](docs/toolchain.md)
- [M0/M1 主 agent 审阅与验证](docs/validation/m0-m1-review.md)
- [M2 编辑与导出验证](docs/validation/m2-editor-export.md)
- [M4 可靠性与分发验证](docs/validation/m4-reliability-distribution.md)
- [使用指南](docs/user-guide.md) / [隐私说明](docs/privacy.md) / [版本说明](docs/release-notes.md)

技术方向：WinUI 3 / C# 应用层，C++ 原生媒体引擎，Windows.Graphics.Capture、WASAPI、Direct3D 11、Media Foundation。

开发时按 `docs/000x-*.md` 的顺序推进。每步完成后更新任务验收项与 `progress.md`，保留构建、自动测试和实机验收之间的区别。

## 下载、安装与发布

版本安装包位于 [GitHub Releases](https://github.com/lucasx001/frametrail/releases)。选择 `FrameTrail-X.Y.Z-x64-Setup.exe` 即可按当前用户安装；也可下载便携 ZIP，完整解压后运行。安装包包含运行时，无需开发工具，支持 Windows 11 x64（最低 build 22621）。当前已公开 v0.0.1；当前 EXE 未签名，Windows 可能显示未知发布者或 SmartScreen 提示。

维护者可在 **Actions → Release Windows application → Run workflow** 输入 `0.0.1`，或推送 `v0.0.1` tag，自动构建、测试、打包并发布对应版本。支持先保留草稿，已公开版本不会被覆盖；下一版使用 `0.0.2`。工作流已完成 v0.0.1 公开与附件验证。操作与更新说明见 [分发文档](docs/distribution.md)，执行状态见 [0019](docs/0019-github-releases.md)。

也提供 **Build Microsoft Store MSIX** 工作流：注册商店应用并配置三个身份变量后生成提交包，由 Microsoft 在通过认证后签名，无需自购代码签名证书。尚未注册可开启 `test_only` 生成打包测试 MSIX。配置、版本映射和上传步骤见 [Microsoft Store 分发](docs/microsoft-store.md)。

## 构建与启动

Windows 11 x64，准备工具链后在本目录运行：

```powershell
.\scripts\build.cmd -Configuration Release
.\scripts\run.cmd -Configuration Release
```

`.cmd` 入口适用于默认禁止脚本的 Windows PowerShell，通过独立子进程执行对应 `.ps1`；执行策略仅对子进程生效，不修改用户或系统的持久设置。若直接运行 `.ps1` 遇到“在此系统上禁止运行脚本”，请使用以上命令。

默认测试排除需要实际设备的托管检查；实机补充运行 `.\scripts\test.cmd -Configuration Release -IncludeHardware`。具体依赖、原生输出目录和验证命令见工具链文档。

当前是开发候选版。摄像头自动分段已复用持续采集源；60 FPS 长时表现、30 分钟联合音画同步及外设矩阵尚未全面验收，请查看规格中的实现边界。

## M4 候选包与回归

```powershell
.\scripts\package.cmd -OutputDirectory artifacts\packages\my-candidate
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\my-candidate -SmokeTest
# 登录桌面上执行真实录制、编辑和导出；会记录测试区域画面
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\my-candidate -WorkflowTest
.\scripts\validate-m4.cmd -DurationSeconds 60
.\scripts\validate-recovery.cmd
```

输出自包含便携 ZIP、未签名 MSIX、依赖/载荷清单与 SHA-256。未签名 MSIX 仅供开发验证，便携包解压后运行 `FrameTrail.App.exe`。增加 `-BuildInstaller` 可生成每用户 Setup.exe；GitHub Release 流程默认包含安装器并排除未签名 MSIX。脚本保留旧产物，每次使用新目录；工具准备、可选已有证书签名和 App Installer 来源配置见 [分发说明](docs/distribution.md)。正式签名、干净机器安装/升级/卸载和公开发布状态分别记录。

## 编辑与导出

录制完成后点击“开始编辑”，或在录制条的更多菜单打开工程目录内的 `project.json`。也可以直接启动：

```powershell
.\scripts\run.cmd -Configuration Release -ProjectPath "C:\Videos\Demo.frametrailproject"
```

编辑器支持分割、裁剪、重排、恢复、0.25–4 倍速、撤销/重做、画布与摄像头布局，以及系统音/麦克风的全局和片段音量。提交编辑后自动保存，保存失败保留内存编辑并提供重试。导出支持 H.264/AAC MP4、M4A 和工程素材副本；先写临时文件，成功校验后替换目标。

M2 媒体验证入口（额外需要本机 ffmpeg/ffprobe 和 Python，仅用于测试，不是产品运行依赖）：

```powershell
.\scripts\validate-m2.cmd -Matrix
```

M3 效果回归（先按相同原生目录构建 Release，同样需要 ffmpeg/ffprobe/Python）：

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\validate-m3.cmd
```

在编辑器「运镜」页添加或按录制点击生成效果，选择片段后调整参数即可实时预览，无需点击应用。滑块拖动过程中预览，松手后自动保存并合并为一次撤销；数字/文字输入短暂停顿后保存，输入焦点保持。当前播放头不在效果有效区间时，会定位到该效果的可见时刻。时间线可选择效果，再拖动片段或两端裁边；Ctrl+C / Ctrl+V 复制到播放头，Delete 删除当前效果，Ctrl+Z / Ctrl+Y 撤销重做。效果按源时间保存，剪辑和变速通过统一映射同步处理。

「画面」支持工程内图片、循环视频和渐变背景，「样机」选择窗口/浏览器/设备边框；「开场」设置屏幕或摄像头的动画，「摄像头」选择形状，「光标」调整平滑。场景预设菜单可应用内置预设，或保存/恢复当前工程预设。素材复制进工程后重开无需原始导入路径；视觉预设保留当前声音设置。

## Android USB 录屏（M5）

首次准备运行组件，然后重新构建并启动：

```powershell
.\scripts\setup-android.cmd
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\run.cmd -Configuration Release
```

在来源轮盘选择「手机」，或从更多菜单打开「Android USB 录屏」。手机通过数据线连接后，开启开发者选项和 USB 调试、解锁并允许本机授权，选择设备并连接预览，再开始录制。电脑麦克风在录制条上选择；手机内部音频在手机连接面板单独设置。预览默认静音。Android 11+ 才能尝试内部音频，不可用时可明确选择继续无声视频或取消。应用不会自动安装 USB 驱动或改动手机设置。

手机原始画面通过版本固定的 scrcpy server 直接传入 C++，以 H.264 保存；声音保存为独立 WAV。录制期间方向固定，暂停时继续排空 USB 流，恢复/分段等待下一个关键帧。断线后可使用已完成分段；断开手机后工程仍可编辑、预览和导出。首轮不支持无线连接、手机遥控、动态旋转连续录制、Windows loopback 与手机音频同时采集。手机不会产生 Windows 光标或虚构触摸事件。

无需手机的自动验证（真实 H.264/PCM、协议模拟端、恢复与独立解码）：

```powershell
.\scripts\validate-m5.cmd
```

生成带 .NET/Windows App SDK、固定 Android 组件及许可的本地便携测试包：

```powershell
.\scripts\package-android.cmd
```

以上是本地开发/测试交付，不代表干净机器、真实手机、30 分钟音画同步、安装更新或 M4 发布验收通过。
