# FrameTrail

面向 Windows 11 的原生录屏与演示视频编辑软件。

项目以 DogSC 的产品能力和领域模型为参考，在 Windows 上独立实现采集、编辑、预览和导出。当前已实现 M0 工程基础与 M1 录制原型：屏幕、独立音轨、光标、标记、分段保存和工程恢复。编辑预览与成片导出属于后续 M2。

- [产品规格](docs/spec.md)
- [实现进度与任务顺序](docs/progress.md)
- [工具链与构建](docs/toolchain.md)
- [M0/M1 主 agent 审阅与验证](docs/validation/m0-m1-review.md)

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
