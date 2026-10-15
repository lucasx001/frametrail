# DogSC 整体 UI 复刻记录

日期：2026-10-15。范围：在已完成的录制 UI 基础上，将 M2 编辑器、检查器、时间线、导出与设置整体对齐相邻 DogSC 项目。媒体、工程格式与 M3 的功能边界不因外观复刻而升级。

## 参考依据与实现

参考图为 `dogsc/docs/images/editor.png`；布局与细节以当前 `Sources/DogSCApp/EditorView.swift`、`EditorDesignTokens.swift`、`EditorInspectorPalette.swift`、`EditorInspectorTabBar.swift`、`EditorInspectorView.swift`、`EditorBarSlider.swift`、`EditorTimelineLayout.swift`、`EditorExportViews.swift` 和 `AppSettingsView.swift` 为依据。

- 46 DIP 顶栏：项目菜单、名称、保存标记、撤销/重做、外观/设置与蓝色导出胶囊。Windows 系统窗口按钮保留平台样式。
- 银灰 / 石墨中性底色、18 DIP 点阵舞台、真实比例画布、44 DIP 悬浮播放胶囊，去除上一版左侧三项工具栏和分块深灰卡片。
- 340 DIP 检查器、60 DIP 导航、44 DIP 点击目标、仅当前项显示名称；按用户后续调整，六项导航和页内参数统一使用蓝色强调，不再随检查器页签切换主题色。
- 背景 / 布局 / 样机子导航、18 DIP 内部卡片、30 / 24 DIP 数值条、同色开关；参数接入既有工程命令、自动保存及撤销。
- 通栏时间线、薄分隔线、固定轨道标识、重复缩略图、片段时长角标、绿色混音波形、红色播放头；支持画面 / 波形切换、吸附开关、完整时间线缩放。播放只移动播放头，避免逐帧重建缩略图元素。
- 820 DIP 双栏导出面板：内容 / 规格与媒体摘要、蓝色操作胶囊、面板内进度 / 取消 / 完成结果；继续使用原生系统保存选择器和既有可靠导出路径。
- 560 DIP 设置画布、56 DIP 导航胶囊和 496 DIP 页面岛；提供外观、项目位置、快捷操作、录制 / 设备配置说明及第三方许可。外观同步至录制条、完成卡片和备忘录，偏好保存在本机。
- PuHuiTi 字体沿用此前未修改的字体资产；DogSC 的 MIT 署名已扩展覆盖编辑器设计来源。

开场、运镜、样机、场景预设、壁纸 / 图案 / 动态背景等当前引擎未支持的能力保持不可用提示。不能把六项导航的出现计为 M3 已实现，也不能把合成测试素材当作真实摄像头验收。

## 验证与证据

Release 构建通过，0 警告 / 0 错误。既有 4 组原生测试和 101 项默认托管测试通过。本轮未重新执行音频 / 摄像头硬件测试。

本地忽略目录 `artifacts/validation/dogsc-ui-05/check/` 保存应用级检查结果、浅色 / 深色 / 紧凑窗口 / 导出面板渲染图、GPU 预览图及关闭生命周期日志。测试使用独立工程副本。

- 六项导航与背景 / 布局切换不会修改工程修订。
- 参数条修改经既有保存路径落盘，保留画布分辨率，并能用一次撤销精确恢复。
- 840×600 DIP 紧凑布局下，预览、检查器与播放胶囊有可用空间。
- 真实 MF 解码、seek generation、播放、分割 / 变速 / 重排、撤销 / 重做、取消事务、自动保存、万片段可见区绘制与正常关闭均纳入应用级检查。

`RenderTargetBitmap` 不包含 SwapChainPanel 像素，因此 UI 导图中的黑色预览区不是播放缺失；真实合成画面独立保存在 `preview.png`。Windows Computer Use 已观察到真实视频画面和新编辑器，并检查背景 / 布局切换与导出面板。OS 鼠标完整拖动、系统文件选择器导出、多显示器与多 DPI 的完整人工矩阵仍待验收。

这是依照参考截图及设计源码的 Windows 原生复刻；没有在本机运行 macOS DogSC 做逐像素截图差分。Windows 字形栅格化、原生窗口按钮、系统选择器与暂不可用功能会形成可见差异，不宣称全平台逐像素完全相同。

## 用户截图反馈：悬浮条外框、黑边与断续圆弧

用户提供的两张实窗截图补足了控件导图看不到的原生窗口问题：第一版存在矩形系统外框与粗黑圆弧；仅移除外框后，左右细描边仍被整数区域裁剪切断。

- 原生测量：115 DPI 下窗口 455×74，客户区 449×68；实际保留 `WS_DLGFRAME` 和 `WS_EX_WINDOWEDGE`。仅调整 `OverlappedPresenter` 调用顺序无法消除。
- 修复：移除实际非客户区样式、重新计算客户区；禁用 DWM 的额外边框 / 圆角；窗口与客户区现在同为 449×68。
- 用透明 `SystemBackdrop` 与 DWM alpha 背景承载 XAML 的抗锯齿圆角。GDI 区域仅限制输入范围，并向外留 2 物理像素，避免切掉 XAML 的 1 DIP 描边。透明处理参考 WinUIEx，MIT 声明随应用附带。
- `artifacts/validation/dogsc-ui-04/chrome-alpha-check/chrome-validation.json`：浅 / 深色均验证四角 alpha 为 0、窗口 / 客户区一致、全部可见像素落在输入区域内，裁剪可见像素数为 0；正常退出。
- 修复关闭直接打开的编辑器后未初始化录制设备的问题；返回悬浮条时会初始化设备、快捷键与窗口排除状态。

复现入口：`FrameTrail.App.exe --chrome-self-test <输出目录>`。这是当前 DPI 的窗口几何、像素 alpha 与区域检查，不能代替完整跨显示器 DPI 实窗验收。

## 用户反馈：统一检查器颜色与页签对齐

- 六个检查器页签及页内参数条 / 开关统一为现有蓝色强调。
- 修正共用按钮模板：内容内边距只作用于内容层，悬停层与背景共用完整尺寸和圆角，移除负边距补偿。
- 背景素材的四个选项使用等宽星号列与拉伸按钮，文字水平 / 垂直居中，选中底色铺满对应选项；按钮高 32 DIP。
- 不改变壁纸、网格、动效当前不可用的功能状态。

本次 Release 构建为 0 警告 / 0 错误；复用已有应用检查入口，`artifacts/validation/editor-tabs-01/check/editor-validation.json` 通过，浅 / 深色及紧凑布局渲染图保存在同目录。控件导图中背景四项已等宽且文字居中。此次没有重新运行原生 / 托管 CPU 测试或硬件矩阵。

Windows Computer Use 实窗切换到声音页：选中胶囊、参数条和开关均为蓝色，悬停底色覆盖同一胶囊轮廓；已观察背景素材四项的等宽居中布局。

## 用户反馈：编辑器切换明暗主题卡死并闪退

2026-10-15 实窗点击顶部外观按钮，复现进程退出。Windows 记录 `Microsoft.UI.Xaml.dll` / `0xc000027b`，WER 原始错误为 `80070057`。加入不拦截异常的日志后，捕获到 `System.ArgumentException: target`，调用栈指向 `SystemBackdrop.OnDefaultSystemBackdropConfigurationChanged`。

主题按钮除了改变编辑器，还会保存偏好并广播给隐藏的录制窗口。WinUI 在隐藏目标尚未连接时传入空目标，透明背景没有覆写该回调，默认基类拒绝空参数。现按 [WinUIEx CompositionBrushBackdrop](https://github.com/dotMorten/WinUIEx/blob/main/src/WinUIEx/CompositionBrushBackdrop.cs) 的处理，仅在目标非空时调用基类；保留已连接目标的正常处理和胶囊透明背景。未吞掉异常或禁用主题切换。

此前检查只直接赋值编辑器的 `RequestedTheme`，没有覆盖完整保存 / 广播流程。已将既有 `--m2-self-test` 扩展为调用产品按钮处理器：12 次连续切换，断言偏好落盘和工程内容完全不变；播放中再切换 6 次，断言画面继续推进。结束时恢复原有外观偏好。未修复版本在此入口的第 2 次切换复现同一异常。

修复后证据保存在 `artifacts/validation/theme-crash-02/`：

- `before-fix-crash.log` 保留原始异常调用栈。
- `check/editor-validation.json` 全部通过：12 次切换、播放中 6 次切换（继续渲染 15 帧）、预览 / seek、剪辑 / 撤销 / 保存与关闭生命周期正常。
- `chrome/chrome-validation.json` 浅 / 深色通过：窗口与客户区均 449×68，四角透明，无可见描边像素被裁剪。
- Release 构建 0 警告 / 0 错误；4 组原生测试、101 项默认托管测试通过。
- Windows Computer Use 实窗连续点击顶部按钮 4 次，并从设置页切换浅色 / 深色 / 跟随系统，进程保持响应。

异常详情现保存在 `%LOCALAPPDATA%/FrameTrail/crashes/`，不改变未处理异常的退出行为。本轮未扩大到系统主题自动变化、多 DPI 或音视频设备矩阵验收。

## 用户反馈：slider 与 switch 参考 shadcn/ui

按 [shadcn/ui Slider](https://ui.shadcn.com/docs/components/slider) 和 [Switch](https://ui.shadcn.com/docs/components/switch) 的官方源码重做原生控件，保留蓝色编辑器强调和录制条现有绿色强调。

- 参数 slider 改为 6 DIP 轨道、16 DIP 白色圆形滑块，数值独立右对齐；悬停 / 键盘焦点光环使用 150 ms 过渡。拖动直接跟手，按住滑块边缘时保留抓取偏移，松开只提交一次；支持方向键、Home / End、PageUp / PageDown，Esc 可取消拖动。
- 编辑器与录制弹层开关共用 32×20 DIP 轨道、16 DIP 滑块和 160 ms 缓出过渡。位移与底色透明度同时变化，快速反向切换可正确结束。保留原生 CheckBox / ToggleButton 的键盘、点击与无障碍语义；关闭 Windows 动画时直接应用最终状态。
- 在隐藏页签首次测量 / 展开时同步开关状态，避免模板尚未创建时收到保存值却仍显示默认关闭状态。
- 开关持有焦点时按空格不再触发编辑器播放快捷键。MIT 署名已随应用附带。

`artifacts/validation/controls-04/check/` 保存浅 / 深色图和动画采样。实际位移在约 30 / 62 / 92 / 123 / 155 ms 时依次为 4.20 / 7.71 / 10.48 / 11.70 / 11.99 DIP，随后到达 12 DIP；轨道透明度同步从 0 增至 1。快速反向、参数保存 / 单步撤销、18 次完整主题切换和 M2 编辑闭环均通过。

Windows Computer Use 在独立工程中实际拖动摄像头大小 25% → 39%，落盘只增加一个修订；方向键再调整至 40%。实际点击及空格切换开关后，视频播放位置保持不变。

## 用户反馈：X / Y 输入失焦卡住

原来的 NumberBox 使用 `Compact` 步进弹层；数字子控件的 `LostFocus` 又会直接触发保存、关闭检查器命中并重载全部参数。该调用路径会在 X → Y 的焦点切换期间干扰 Y 的编辑。原生 `Compact` 模式会在聚焦时显示弹层，见 [WinUI NumberBox 说明](https://github.com/microsoft/microsoft-ui-xaml/blob/main/specs/NumberBox/NumberBox.md)。

- 改用框内步进按钮，避免浮层覆盖相邻输入。
- 失焦提交在焦点 / 数字验证完成后排队；在数字输入组之间切换时保留草稿，离开输入组再统一提交。输入框内部子控件间切换不会触发整个检查器重载。
- Enter 和点击空白处都可以结束编辑；关闭或导出前会提交尚未失焦的数值。
- 继续使用原有验证、自动保存与撤销路径，X / Y 的一次编辑只产生一个撤销步骤。

`artifacts/validation/number-input-01/check/editor-validation.json` 通过，包含 X → Y 焦点与草稿保留、两个值统一落盘、单次撤销、关闭 / 导出前提交，以及 slider / switch、主题和 M2 回归。实窗输入 X=30、Tab 切到 Y 输入 40，再点击空白处：焦点正常离开，项目的 X / Y 保存为 0.3 / 0.4，预览更新且界面继续响应。该实窗检查使用独立工程，不修改用户原有工程。

增加隐藏开关首次展开状态断言后，最终应用回归 `artifacts/validation/number-input-02/check/editor-validation.json` 同样通过。

实窗结果及修订号记录在 `artifacts/validation/number-input-01/interaction-validation.json`。本轮正式 Release 构建为 0 警告 / 0 错误，输出程序集与通过上述回归的隔离构建哈希一致。未重跑原生 / 托管 CPU 测试或硬件矩阵。
