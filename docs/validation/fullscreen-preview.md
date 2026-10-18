# 编辑器全屏视频预览

日期：2026-10-16。Windows 11 x64，WinUI Release。

## 实现

- 播放胶囊右侧增加全屏按钮，支持 F11、双击视频切换及 Esc 退出；无障碍名称随状态切换为「全屏预览」/「退出全屏」。
- 使用 Windows 原生 FullScreen presenter，视频按画布比例完整适配屏幕；全屏收起标题栏、检查器、时间线和画面编辑入口，仅保留播放 / 暂停、逐帧、时间和退出按钮。
- 在同一 XamlRoot 内移动现有预览与控制，不重新创建媒体会话、解码器、声音或 swap chain。保留当前播放位置，退出时恢复普通 / 最大化窗口、编辑器预览倍率和滚动位置。
- 全屏只处理播放相关快捷键，避免隐藏编辑界面时误分割或删除片段。预览错误会返回编辑器以显示提示。

## 应用内验证

验证入口为 `FrameTrail.App.exe --editor <测试工程> --fullscreen-self-test <新输出目录> --inspect-ui`，只能使用可丢弃的工程副本。

最终证据：`artifacts/validation/fullscreen/self-test-final/fullscreen-validation.json`，`success: true`，由标准启动目录的 Release 程序执行。

- 原生全屏 presenter、编辑器控件收起、视频完整适配、不裁剪与控制宿主检查通过。
- 暂停位置、退出后恢复 200% 预览倍率与原窗口位置 / 尺寸。
- 浅 / 深色反复切换、最大化进入后仍恢复最大化通过。
- 播放中进入 / 退出全屏后均有新的实际合成帧完成，媒体会话及 swap chain 身份不变；工程 JSON 与 revision 完全不变。
- 初版检查固定等待 150 ms，遇到异步 swap chain resize 尚未完成而误判。补充状态显示播放一直保持开启，改为在 1.5 秒上限内等待实际完成帧，验证通过。
- Release 构建 0 警告、0 错误。

实际 Windows 窗口通过 Computer Use 验证：点击全屏按钮、Esc 返回、F11 进入、双击视频退出及再次双击进入。全屏截图可见合成视频与底部播放控件，编辑器侧栏 / 时间线消失；返回后编辑布局恢复。切换前关闭按钮提示，避免悬浮提示留在旧位置。

全屏播放快捷键在 PreviewKeyDown 阶段处理，确保退出按钮获得焦点后，空格仍切换播放 / 暂停，左右键仍逐帧，避免按钮把空格解释为点击。

`fullscreen-controls.png` 来自 WinUI RenderTargetBitmap，只能证明控件布局；其中 SwapChainPanel 区域不包含视频像素。视频像素由 `before.png` / `fullscreen.png` 独立记录，并需结合实际窗口检查。

跨显示器 / 多 DPI、HDR 与长时间全屏播放不包含在本轮短测中。

## 全屏模糊修复与 4K 预览

旧检查的进出全屏 PNG 均为 640×360，逐字节一致只证明内容一致，没有证明屏幕像素足够。根因是 `ft_media_render` 按工程画布尺寸生成合成图，且最多 1920×1080；`Renderer::present` 将整图放大到 swap chain，导致新绘制的样机边框、圆角与网址一并模糊。

现在 `Renderer::resize` 记录物理视口尺寸，自动预览按该尺寸等比重绘，包含 SwapChainPanel 的 CompositionScale。上限按画布方向为 3840×2160 或 2160×3840；菜单「预览清晰度 → 4K · 高清预览」可在小窗口也使用 4K 渲染。切换通过独立 C ABI 更新会话中的质量参数，重载时保留选择，不写入工程。导出仍使用显式导出尺寸。

新增证据：`artifacts/validation/preview-4k/self-test-02/fullscreen-validation.json`，`success: true`；Release 构建 0 警告 / 0 错误，7 组原生与 181 项托管测试通过。

- 附着全屏预览 PNG 为 2558×1439，与显示区域物理像素一致，且与同尺寸的新会话渲染逐字节一致；强制 4K 为 3840×2160。
- 返回相同编辑器倍率后，PNG 与进入前逐字节一致；保留暂停位置、窗口边界、明暗及最大化状态。
- 横竖画布分别覆盖 961×541 / 150% DPI、2560×1440、3840×2160、2160×3840、5000×3000 视口；检查等比尺寸、4K 上限、自动模式恢复及非法质量拒绝。
- 在 4K 预览模式下导出，MP4 仍为明确设置的 640×360；播放中切到 4K 并进出全屏后实际完成帧继续增加，会话与 swap chain 身份、工程 JSON 均不变。
- 检视 `4k.png`：样机圆角、按钮及网址清晰，640×360 原视频仍保留原有分辨率限制。
- `artifacts/validation/preview-4k/mockup-02/mockup-validation.json` 为 `success: true`：六样式及自定义样机导出、实时网址 / 旋钮 / 取消 / 撤销像素对比、Unicode 元数据与紧凑布局通过。复用工程可能已有目标标题，测试先确保文本不同，再验证真实编辑事件。
- `artifacts/validation/preview-4k/m3/result/independent-media-verification.json` 为 `success: true`：28 组成片回归通过，最差 MAE 2.673，最低 PSNR 28.88 dB。
- 新版实际窗口的无障碍树已确认「预览选项」入口。初次因 Windows 锁屏暂停，解锁后已通过 Computer Use 点击「预览清晰度 → 4K · 高清预览」、进入全屏并按 Esc 返回；实际画面与底部播放控件正常，标题与边框清晰，返回后编辑器布局恢复。

本次 DPI 测试覆盖 native resize 参数，不替代实际跨显示器迁移。4K 长时性能及复杂效果下的 60 FPS 仍需独立性能验收。

## 2026-10-18 — 普通窗口默认 4K

用户反馈未全屏的编辑预览仍模糊。现有 UHD 渲染路径可在小窗口使用，但编辑器默认枚举值为 `Automatic`，按较小视口生成低分辨率合成图。`PreviewSurface` 现在显式默认 `UltraHd`，预览菜单优先显示「4K · 高清预览（默认）」，仍可手动选择自动模式。会话重载保留当前选择，不改变缩略图 / 分析会话的默认策略或工程 / 导出参数。

`Renderer::present` 使用 `D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC` 把合成图缩放到物理 swap-chain 视口，为 4K 缩小提供高质量预滤波；离线导出仍使用原来的显式尺寸与绘制路径。这里的 4K 是内部合成分辨率，小窗口最终可见的像素数仍由显示区域决定，原视频不补造细节。

验证命令：`scripts/build.cmd -Configuration Release`，以及新构建的 `FrameTrail.App.exe --editor <可丢弃工程副本> --fullscreen-self-test <输出目录> --inspect-ui`。

- Release 构建 0 警告 / 0 错误；7 组原生与 201 项默认托管测试通过，硬件分类不包含在默认托管测试中。
- `artifacts/validation/windowed-preview-4k-20261018/result/fullscreen-validation.json` 为 `success: true`。
- 首次普通窗口、缩小窗口、200% 缩放、进入全屏、返回普通窗口与重新加载六张 `windowed-*.png` 均为 3840×2160，SHA-256 均为 `1EF09DAE5C5DAC8298E983B84E40A4160E24F4F67D3A766101F0DCA97D92760F`，证明合成像素不随视口下降。
- 原自动模式的横 / 竖屏、DPI 参数、4K 上限、无效参数拒绝、全屏恢复和新会话像素对比继续通过；4K 预览下的测试 MP4 仍为 640×360。
- 4K 播放进出全屏均有实际完成的新帧，媒体会话与 swap chain 身份不变，工程 JSON 不变。
- 已检查默认 4K 合成 PNG，样机边框与标题重新绘制；测试源仅 640×360，其画面文字仍保留素材分辨率限制。
- Computer Use 已在用户当前安装版编辑器中操作「预览选项 → 预览清晰度 → 4K · 高清预览」，播放位置仍为 00:33.03，未显示预览错误。开发版与安装版同时运行时，工具把两个窗口映射到相同应用标识，重取窗口仍报 `window id 723796 no longer belongs to ...; current owner is ...`，因此本轮新版实际窗口的额外鼠标 / 截图验证未完成。已关闭本轮可丢弃的额外测试进程，保留用户安装版窗口。

PNG 验证记录的是合成纹理，不能单独证明最终 swap-chain 缩小画面的观感。高质量缩小的实屏对比、跨显示器 DPI 和 4K 长时性能仍须补充；本轮没有替换安装版文件或发布新安装包。
