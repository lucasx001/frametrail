# M3 — 运镜与视觉包装验收

日期：2026-10-15。范围：0009。本机功能闭环完成；M1/M2 未完成的硬件、听测和长时矩阵保留，联合性能进入 0010。本轮未创建提交或推送。

## 参数实时预览补充（2026-10-15）

按反馈移除「应用效果」。运镜页的缩放/位置/大小/遮挡强度/透明度/倾斜/旋转/弹簧、曲线/附着/对象选项和贴图文字均在有效输入时预览。输入合并到约 33ms 的更新窗口，滑块按住期间不写工程，松手后自动保存为一次历史；数字/文字在 400ms 输入间隙保存。提交不重建控件或媒体会话，数字焦点保留；取消滑块恢复原效果，无更改的事务不增加历史。切换效果、撤销、导出及关闭先刷新待提交参数。预览时若播放头位于效果外或透明的首末端，会定位到该效果可见区间，快速点击和短拖动也生效。

新增原生 `ft_media_update_effects` 仅替换经过完整校验的效果和修订，复用解码器、D3D 设备、shader/图片缓存及 swap chain，代际检查拒绝过期更新。失败保留上一份完整场景；界面参数修改走 Workspace 事务，预览候选不写盘，后台缩略图在提交后更新，声音缓存保留。

- `artifacts/validation/m3/live-04/ui/m3-editor-validation.json`：十次连续参数变化在提交前产生不同像素，保持同一 MediaSession；一次提交/撤销/重做覆盖整个手势。取消、快速编辑定位、有效数字实时输入及焦点保留通过，原 M3 属性/预设/主题/紧凑布局流程继续通过。
- `artifacts/validation/m3/live-media-01/result/m3-validation.json`：12 次原生热更新保持同一 swap chain，过期/无效更新原子拒绝，回退和新建会话的画面逐像素相同；原有 28 组预览/MP4 独立检查再次通过。
- `artifacts/validation/m3/live-tests.log`：6 组原生、110 项默认托管测试通过，增加无变化事务不产生修订/撤销的检查。独立 Release 构建 0 警告/错误。

独立新版输出为 `artifacts/live-effects-app`，`Run-FrameTrail.cmd` 为本机工具链启动入口。当前用户仍打开旧版编辑器，原输出文件被占用，因此保留旧窗口；关闭旧窗口后可启动此新版并重开工程，或重新执行标准 Release 构建更新常规启动目录。

## 实现与使用

- `core/effects.cpp` 按 DogSC AutoZoomPlanner 的点击时间/空间分组、活动延长和相邻包络合并规则规划运镜。固定来源标记，重生成只替换自己的自动片段；手工修改标成 edited。相同输入可重复执行，移走的手工片段 ID 也不会与重生成冲突。参考版本与 MIT 声明见根目录 `THIRD_PARTY_NOTICES.md`。
- 原生 FrameScene 根据唯一输出→源映射求值线性/平滑/闭式弹簧、进入/保持/退出和焦点轨迹。光标使用因果指数平滑；没有依赖 UI 调用频率或系统界面动画偏好。缩放焦点属于裁切并 fit 后的屏幕内容，投影前转换到包含留白的画布坐标；摄像头围绕自己的布局中心缩放。
- 共享 `Renderer::render` 进入 D3D11/HLSL 透视、聚焦、像素化/柔化/实心区域，Direct2D/DirectWrite 绘制屏幕/摄像头、边框、文字和图片贴图。预览和导出使用同一路径；透明图层保持预乘 alpha。遮挡处理顺序固定，实心遮挡不会被后续模糊重新露出。
- 背景图片/循环视频、文字/图片贴图、窗口/浏览器/设备框、圆角/矩形/圆形摄像头，以及淡入/上移/缩放/滑入开场均可保存。导入先复制进 assets/ 再经 WIC/MF 探测；受尺寸、文件大小和重叠数量限制。
- 「运镜」页支持添加、选择、参数实时预览、复制、粘贴到播放头、删除；效果分六类时间线轨道，可再次按住选中片段移动或拖两端裁边，一次释放只入一次撤销历史。Ctrl+C/V、Delete 和 Ctrl+Z/Y 可用，数字/文本输入保留自己的键盘行为。
- 「开场」「光标」「摄像头」「画面/样机」提供相应设置，沿用蓝色强调、圆角卡片、现有 slider/switch 和主题。添加开场的摄像头源起点独立于屏幕起点。场景菜单提供简洁/工作室/设备三种预设及工程内保存/恢复；视觉预设不覆盖声音设置。
- 视觉素材参与保存、重开和显式恢复检查，源素材副本导出包含 assets/ 与预设；正常打开遇缺素材会报错，显式恢复才退到上一份可读快照并报告原因。

参数与图层约定见 [schema](../schema.md)。实现采用 [D3DCompile](https://learn.microsoft.com/en-us/windows/win32/api/d3dcompiler/nf-d3dcompiler-d3dcompile) 和 Direct2D 的[预乘 alpha 格式约定](https://learn.microsoft.com/en-us/windows/win32/direct2d/supported-pixel-formats-and-alpha-modes)；不增加产品的 FFmpeg 运行依赖。

## 已通过证据

环境为 Windows 11 build 26300 / x64、本机 D3D11/MF。测试素材均由回归脚本生成，不依赖用户录屏。

| 范围 | 验证结果及证据 |
|---|---|
| 构建/默认测试 | Release 锁依赖构建 0 警告/错误；6 组原生、109 项托管测试（App 6、Capture 6、Domain 69、Workspace 28）；`artifacts/validation/m3/final-build-tests.log` |
| 自动规划/源时间 | 原生效果专项 330 检查：18% 提前进入、空间组焦点、连续活动合并、幂等、编辑保护、ID 冲突、变速/重排映射、边界焦点保留、半开区间、无效参数/拖动边及 32 区域限制；录制 JSONL 经 Workspace 读取规划也通过 |
| 单效果/组合 | `run-05/result/m3-validation.json`：28 组预览 PNG 与 3 秒 MP4；zoom/tilt/三种 mask/focus/图片和文字 sticker/四种 intro/摄像头动画/三种摄像头形状/三种 frame/三种 backdrop/组合/画布遮挡/重叠隐私遮挡 |
| 独立图像对比 | `run-05/result/independent-media-verification.json`：ffmpeg 独立解码每个 MP4；30 FPS 共 90 帧、60 FPS 共 180 帧、均 3 秒，BT.709 标记正确。预览与同时间成片最差 MAE 2.598、最低 PSNR 28.88 dB；阈值为 MAE <3.5、PSNR >27 dB，包含 H.264 4:2:0 饱和测试图色度损失 |
| 坐标/alpha/隐私 | 独立检查实心矩形及重叠模糊仍为全黑；focus 中心保留、外围柔化；画布遮挡盖住摄像头。半透明红色贴图符合 0.5×0.8 alpha 混合；缩放+倾斜光标差分范围 (396,165)–(419,202)，独立投影笔尖约 (400.02,168.72) |
| 时间轨迹 | 同一 1.5 秒处低频与高频预览 PNG 完全相同，与 60 FPS 成片第 90 帧比较 MAE 1.396 / PSNR 34.41 dB；Core 同时检查不同采样历史、曲线端点和焦点连续性 |
| 保存与迁移 | 效果复制/撤销/重做内容一致；预设保存恢复且保留音量；同时引用图片背景和贴图后导出工程副本，重开与原工程 PNG 完全相同。缺视觉素材保存拒绝/同修订重试和显式恢复都有持久化单测 |
| WinUI | `ui-06/ui/m3-editor-validation.json`：五种手动效果、屏幕/摄像头开场、效果选择/移动/粘贴/历史、预设、图片背景换色/撤销、12 次明暗切换和 840×600 DIP 布局通过。`motion-before-theme.png` 与 `motion-after-theme.png` 完全相同；UI 动画偏好没有进入项目/原生求值输入，未操作 OS 全局偏好 |
| 实际输入 | 对临时工程实际鼠标点击运镜、滚动效果轨道、拖动缩放：源起点 1 秒移至 0.6116971 秒，时长保持 2 秒，释放自动保存，点击一次撤销恢复 1 秒。随后播放到 2.99 秒，组合画面与界面可操作；`ui-03/ui/actual-playback.jpg` |
| M2 回归 | `m2-regression` 的媒体、声音/六档音调、取消、占用和素材副本回归及独立 ffmpeg 检查通过；`m2-ui-regression/ui/editor-validation.json` 覆盖 switch 动画、数字输入焦点/保存/撤销、主题/播放、导出界面及 10000 片段绘制（413 个可见元素，44.1 ms） |

以上路径均相对 `artifacts/validation/m3/`，构建日志一行使用了完整路径。历史失败/调试轮次保留，最终媒体结果以 run-05 为准，早期 run-01–04 不用来替代最终结果。

## 复跑

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\validate-m3.cmd
```

脚本需要 ffmpeg、ffprobe、Python，只用于生成/检查回归素材。指定输出目录必须尚不存在，避免覆盖旧证据。

应用级验证使用构建输出的 `FrameTrail.App.exe --editor <临时工程副本> --m3-self-test <新证据目录> --inspect-ui`。它会编辑副本并在结束时关闭验证进程；不得传入需要保留编辑状态的用户工程。M2 同类入口为 `--m2-self-test`。原生 swap chain 不在 RenderTargetBitmap 中，应用控件 PNG 的黑色舞台不能作为视频画面证据；使用单独的 preview PNG、MP4 或实际窗口截图。

## 后续验收边界

M3 最后一轮短测中全部六类效果的 WinUI 实际呈现为 103 帧 / 2.114 秒（约 48.73 FPS，640×360，包含调度和后台素材工作）；这是非卡死功能 smoke，尚未达到复杂效果 1080p60 的性能验收。CPU 回读导出沿用 M2；0010 继续测试复杂/长工程、资源峰值、WARP/多 GPU、DPI/HDR 与长时音画同步。不把本机合成素材或缺少 Mac 的静态算法对照当作完整设备/发行验收。光标点击反馈、静止隐藏和时间偏移不在本次 0009 的实现清单中，产品目标仍保留于 spec。
