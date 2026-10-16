# 样机实时设置与点击缩放动效

日期：2026-10-16。本机 Windows 11 x64，Release。

## 样机设置修复

用户修改网址、框体比例、顶栏高度、外圆角后，工程数据已变化，附着的预览却没有同步。原因是样机共用了仅包含 `revision` / `effects` 的 `UpdateEffectsAsync`。现在样机的草稿、提交和取消均使用 `UpdateProjectAsync`，继续复用解码器、媒体会话和 swap chain；运镜参数保留效果热更新路径。

扩展 `--mockup-self-test`：逐项修改输入控件，将**附着预览会话**输出的 PNG 与当前草稿的新建原生渲染逐字节比较；同时要求每项确实改变画面。覆盖网址保留焦点时的连续输入（12 次、间隔 8 ms）、草稿、提交、取消，以及三个旋钮的草稿 / 单次提交 / 撤销 / 重做，验证媒体会话和 swap chain 身份不变。补充六种样机、文字保存、默认样式还原、明暗与 840×600 DIP 布局检查。新输入取消过期渲染时继续消费候选队列，避免仍有待处理输入却停止刷新。

首轮及连续输入补充回归均成功，最终结果为 `artifacts/validation/mockup-motion-fix/ui-final/mockup-validation.json`。独立解码七组 MP4（每组 75 帧），预览 / 导出 RGB 平均误差 0.997–1.095；样式与自定义参数均有可见像素差异，见同目录 `independent-mockup-verification.json`。

## 对齐 DogSC 缩放

依据相邻 DogSC 的 `Sources/RecorderCore/ProjectModel.swift`（MotionStyle）、`ZoomAnimation.swift`（springProgress、cubic Bezier 和进入 / 回落采样）、`AutoZoomPlanner.swift`：

- 保持 0.7 秒进入 / 回落、1.6 倍默认缩放，以及现有点击分组和活动停留规则。
- 将默认弹簧从 3 Hz / 0.85 阻尼比对齐为质量 2.4、刚度 210、阻尼 42 对应的 1.4887581711534412 Hz / 0.9354143466934853；使用相同的 0.72 秒响应归一化与 [0,1] 限幅。
- 回落改为 `1 - ease(已回落进度)`；短片段按实际停留区间限制进入时长，避免将进入压缩到总时长的一半。
- cubic 改为求解控制点 `(0.65,0,0.35,1)` 的时间反函数，不再将 smoothstep 当成同一曲线。
- 已有工程中省略弹簧参数的自动缩放直接使用新默认值；显式保存的用户参数继续保留。没有重新生成或改写用户的运镜片段。

原生回归增加 DogSC 公式的六个固定采样值（误差 < 1e-12）、进入 / 回落方向、短片段完整进入、显式参数保留与曲线端点检查。该结果是源码与数值轨迹对照，不代表已在 macOS 上运行 DogSC 并完成逐像素视频对比。

## 验证

- 原生 Release 构建、7 组 CTest 全部通过；181 项默认托管测试通过（设备测试排除）。
- WinUI Release 构建 0 警告、0 错误，已生成标准启动目录与 `artifacts/mockup-motion-final-app` 最终验证副本。
- Computer Use 实际切换样机页、键盘输入网址、通过数值输入将顶栏调为 1.5，确认网址仍有焦点时预览已更新，顶栏高度也改变当前画面；这些操作使用独立测试工程。
- M3 的 28 组成片完整解码、时长 / 帧数 / BT.709、预览与成片像素、效果叠加和资源迁移通过。最差 RGB 平均误差 2.673，最低 PSNR 28.88 dB。
- 新增进入第 3 / 7 帧和回落第 78 / 82 帧的独立解码对比，平均误差 1.593–2.348，PSNR 30.09–34.14 dB。证据为 `artifacts/validation/mockup-motion-fix-m3/result-phases/independent-media-verification.json`。
- M3 检查最初发现其光标位置计算仍沿用无样机的内容矩形；修正独立计算以计入浏览器顶栏消耗的高度，保留原误差阈值，重新运行通过。

本轮不替代跨 DPI、物理鼠标手感、长时硬件性能或 macOS / Windows 并排主观验收。
