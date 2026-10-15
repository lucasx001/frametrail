# FrameTrail

面向 Windows 11 的原生录屏与演示视频编辑软件。

项目以 DogSC 的产品能力和领域模型为参考，在 Windows 上独立实现采集、编辑、预览和导出。当前已实现 M0 工程基础与 M1 录制原型：屏幕、独立音轨、光标、标记、分段保存和工程恢复。M2 已接入原生预览、非破坏性时间线编辑与 MP4/M4A 导出；扩展实机验收仍待完成，见 [M2 验证记录](docs/validation/m2-editor-export.md)。

移动设备录屏方向为 [通过 USB 数据线连接 Android 手机](docs/0012-android-usb-recording.md)，规划在首版后的 M5，尚未实现。

录屏界面已参照 DogSC 重做为悬浮胶囊与输入配置面板，包含来源选择遮罩、区域拖框、录制条、完成卡片和备忘录。实现与验证边界见 [录屏 UI 记录](docs/validation/0005-recorder-ui.md)。

编辑器、六项检查器、通栏时间线、导出与设置界面也已按 DogSC 截图及设计源码重做，支持统一的明暗外观。M3 已接通点击自动运镜、手动缩放/透视、聚焦与遮挡、贴图、开场/摄像头动画、工程内背景和场景预设，见 [M3 验证](docs/validation/m3-motion-effects.md)；参考来源与许可见 [第三方声明](THIRD_PARTY_NOTICES.md)。

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

M3 效果回归（先按相同原生目录构建 Release，同样需要 ffmpeg/ffprobe/Python）：

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\validate-m3.cmd
```

在编辑器「运镜」页添加或按录制点击生成效果，选择片段后调整参数即可实时预览，无需点击应用。滑块拖动过程中预览，松手后自动保存并合并为一次撤销；数字/文字输入短暂停顿后保存，输入焦点保持。当前播放头不在效果有效区间时，会定位到该效果的可见时刻。时间线可选择效果，再拖动片段或两端裁边；Ctrl+C / Ctrl+V 复制到播放头，Delete 删除当前效果，Ctrl+Z / Ctrl+Y 撤销重做。效果按源时间保存，剪辑和变速通过统一映射同步处理。

「画面」支持工程内图片、循环视频和渐变背景，「样机」选择窗口/浏览器/设备边框；「开场」设置屏幕或摄像头的动画，「摄像头」选择形状，「光标」调整平滑。场景预设菜单可应用内置预设，或保存/恢复当前工程预设。素材复制进工程后重开无需原始导入路径；视觉预设保留当前声音设置。
