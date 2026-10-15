# FrameTrail

面向 Windows 11 的原生录屏与演示视频编辑软件。

项目以 DogSC 的产品能力和领域模型为参考，在 Windows 上独立实现采集、编辑、预览和导出。当前已实现 M0 工程基础与 M1 录制原型：屏幕、独立音轨、光标、标记、分段保存和工程恢复。M2 已接入原生预览、非破坏性时间线编辑与 MP4/M4A 导出；扩展实机验收仍待完成，见 [M2 验证记录](docs/validation/m2-editor-export.md)。

移动设备录屏方向为 [通过 USB 数据线连接 Android 手机](docs/0012-android-usb-recording.md)，规划在首版后的 M5，尚未实现。

录屏界面已参照 DogSC 重做为悬浮胶囊与输入配置面板，包含来源选择遮罩、区域拖框、录制条、完成卡片和备忘录。实现与验证边界见 [录屏 UI 记录](docs/validation/0005-recorder-ui.md)。

编辑器、六项检查器、通栏时间线、导出与设置界面也已按 DogSC 截图及设计源码重做，支持统一的明暗外观。尚未实现的高级视觉能力保持明确的不可用状态，见 [整体 UI 验证](docs/validation/dogsc-editor-ui.md)。

- [产品规格](docs/spec.md)
- [实现进度与任务顺序](docs/progress.md)
- [工具链与构建](docs/toolchain.md)
- [M0/M1 主 agent 审阅与验证](docs/validation/m0-m1-review.md)
- [M2 编辑与导出验证](docs/validation/m2-editor-export.md)

技术方向：WinUI 3 / C# 应用层，C++ 原生媒体引擎，Windows.Graphics.Capture、WASAPI、Direct3D 11、Media Foundation。

开发时按 `docs/000x-*.md` 的顺序推进。每步完成后更新任务验收项与 `progress.md`，保留构建、自动测试和实机验收之间的区别。

## 构建与启动

Windows 11 x64，准备工具链后在本目录运行：

```powershell
.\scripts\build.cmd -Configuration Release
.\scripts\run.cmd -Configuration Release
```

`.cmd` 入口适用于默认禁止脚本的 Windows PowerShell，通过独立子进程执行对应 `.ps1`；执行策略仅对子进程生效，不修改用户或系统的持久设置。若直接运行 `.ps1` 遇到“在此系统上禁止运行脚本”，请使用以上命令。

默认测试排除需要实际设备的托管检查；实机补充运行 `.\scripts\test.cmd -Configuration Release -IncludeHardware`。具体依赖、原生输出目录和验证命令见工具链文档。

当前是开发原型。摄像头周期分段仍有重启间隙，60 FPS、30 分钟联合音画同步及外设矩阵尚未验收；请查看规格中的实现边界。

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
