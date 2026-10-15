# 0005 — DogSC 风格录屏 UI

日期：2026-10-15。环境：Windows 11 x64 build 26300，现有 .NET 8 / WinUI 3 基线。当前 M1 状态保持待验收。

补充实窗修复：用户截图发现的矩形外框、左右黑边及圆弧断续已改为无非客户区边框、透明背景和不裁切描边的输入区域；当前 DPI 下窗口 / 客户区同为 449×68，浅 / 深色可见像素被裁剪数量均为 0。详细证据见 [整体 UI 记录](dogsc-editor-ui.md#用户截图反馈悬浮条外框黑边与断续圆弧)。

## 参考与实现

参考相邻 `dogsc/Sources/DogSCApp/` 的 RecorderSetupSurface.swift、RecorderSurfaceStyle.swift、RecorderViews.swift、RecorderPopover.swift、RecorderPanelInteraction.swift、CaptureDisplaySelection.swift 与 RecordingCompletionWindow.swift。

| 表面 | 对照内容 | FrameTrail 实现 |
|---|---|---|
| 准备条 | 约 374×56 DIP、40 高来源轮盘、44 点击区、36 红色内圆、22 宽握柄 | MainWindow.xaml / RecorderStyles.xaml |
| 录制条 | 48 高、8 点状态灯、计时、标记、备忘录、暂停/继续、36 红色停止 | MainWindow.xaml.cs |
| 配置面板 | 290–300 宽、18 圆角、设备选择、开关、保存配置、更多设置 | WinUI 独立 HWND flyout，关闭根边界约束 |
| 来源选择 | 半透明遮罩、开放角取景框、底部动作胶囊、Esc 取消 | CaptureSelectionController.cs；桌面真实透过 alpha 遮罩显示，不截取桌面作背景 |
| 完成卡片 | 336 宽、320×180 首帧、元数据、两项操作 | RecordingCompletionWindow.cs；保存自动完成，提供继续录制/查看素材 |
| 备忘录 | 40 高工具行、阅读/编辑、字号、收起、原生文本输入 | MemoWindow.cs；内容保存到用户 LocalAppData/FrameTrail/memo.txt |

浅色底色 #FDFDFE、深色 #101113、录制红 #FF453B、输入绿 #4DD98C。按钮保留键盘焦点和可访问名称；呼吸灯遵守系统动画设置。录制配置沿用现有能力，不展示为可用的 ProRes/HEVC、Android USB 或编辑器。摄像头实时预览尚未实现，面板明确说明。

字体文件未经修改，仅随应用加载，不安装到系统。适配来源与 MIT 声明见 `src/FrameTrail.App/THIRD-PARTY-NOTICES.md`，字体完整许可随 Assets/Fonts/LICENSE.txt 一起复制到构建目录。

## 已执行

- Release 锁依赖构建通过，最终 App 编译 0 警告、0 错误。
- 默认回归：4/4 原生测试组与 92/92 托管 CPU 测试通过；未将设备测试或 UI 验证检查数计入 92 项。
- 应用内 UI 验证通过：准备条内容不裁切；来源轮盘切换；麦克风开关/设备项关联；640×360 物理像素区域返回及原生遮罩释放。
- 同一应用内验证调用现有真实录制链路，执行屏幕短录制、标记、暂停/恢复、停止；结果为 2 个主片段、1 个标记，之后经原生媒体检查重开成功。未启用麦克风和摄像头，不能将此项算作联合音画验收。
- WinUI RenderTargetBitmap 导出并实际查看浅色/深色准备条、麦克风设置、保存配置、录制、暂停、选择动作条与完成卡片。已据此修复主题取色导致的对比度问题和客户区内容裁切。
- 用户确认当前实际桌面能看到新版录制条；此项作为桌面可见性的人工验收证据。
- 渲染产物及结构化结果：`artifacts/validation/recorder-ui/flow/`；原始源媒体在该目录的 projects/ 下，仅本地保留。

## 验证边界

Windows Computer Use 本轮可以读取可访问控件树，但激活窗口持续返回 `failed to activate captured window`；截图显示桌面或捕获排除后的黑色。普通 WinUI 隔离窗口也表现相同。用户已确认实际桌面可见录制条，因此不将工具截图异常视为应用不可见的证据；原生点击/拖动仍未完成端到端验收。

配置面板位于独立 HWND，RenderTargetBitmap 不能稳定直接读取其弹出表面；验证入口暂时将同一组控件挂到单独验证窗口导图，然后恢复到产品 flyout。图片证明控件本身的渲染，不证明锚点位置、焦点或外部点击关闭。

独立 HWND 的弹出方式使用 `ShouldConstrainToRootBounds=false`；该能力的官方背景见 [Windows App SDK 1.4 发行记录](https://github.com/MicrosoftDocs/windows-dev-docs/blob/docs/hub/apps/windows-app-sdk/release-notes/windows-app-sdk-1-4.md?pivots=stable)。

仍待人工验收：真实鼠标命中、握柄拖动、屏幕/窗口选择与区域拖框、多显示器负坐标、DPI 切换、Esc/外部点击关闭面板、完整中文输入、录制栏排除视觉效果，以及与运行中的 macOS DogSC 的逐像素对照。已有保存失败重试/损坏 manifest 的 review 问题不在本次 UI 修改范围内。

## 复现入口

普通产品启动：`scripts/run.cmd -Configuration Release`。

`--smoke-test <absolute-result.json>` 保留窗口 Loaded、100 次引擎会话和胶囊高度检查。

开发验证：对构建后的 FrameTrail.App.exe 传 `--ui-self-test <absolute-output-directory>`。此显式入口会生成短录制和 UI 渲染图，最终写 ui-validation.json 并退出；不是默认产品行为。没有全局工具链时，需要为该进程设置本地 portable .NET 的 DOTNET_ROOT。

`--inspect-ui` 仅对当前启动实例关闭录制条捕获排除，以便人工截图；普通启动继续默认排除。验证内容和截图不上传。
