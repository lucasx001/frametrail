# 0014 — 样机界面对齐 DogSC

日期：2026-10-16。状态：进行中。

以相邻 `dogsc/Sources/DogSCApp/EditorMockupTurntable.swift`、`EditorVisualEffectsControls.swift`、`EditorInspectorKnobs.swift` 和 `EditorInspectorView.swift` 的当前源码为依据，复刻样机页的布局、卡片、交互和参数。Windows 原生 WinUI / D2D 实现。

## 验收

- [ ] 六种可循环选择的样式，142 DIP 卡片、118 DIP 转盘、名称和分页点、30 DIP 左右箭头。
- [ ] 拖动、惯性吸附、点击卡片、左右键；遵守系统减少动画设置。
- [ ] 当前画面的缩略图、明暗窗口 / 浏览器和自适应设备框。
- [ ] 还原样式，按样式显示窗口标题 / 网址输入，切换保留文字。
- [ ] 框体比例、顶栏高度、外圆角三个旋钮与数字输入；条件显示和范围与源码一致。
- [ ] 实时预览、一次操作一次撤销、保存重开、旧样机值兼容。
- [ ] 样机的样式、比例、圆角和文字进入共享预览与导出渲染。
- [ ] 构建、领域 / 原生协议、实际 WinUI 明暗与交互、独立媒体验证。

验证结果写入 `docs/validation/mockup-inspector.md`。不将源码对照宣称为已在 Windows 上运行 macOS DogSC 的逐像素对比。
