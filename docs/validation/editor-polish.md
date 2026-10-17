# 编辑器媒体准备、光标、连续缩略图与预设管理验证

日期：2026-10-17。分支：`codex/editor-media-polish`，基于 `main` 的 `0e788a3`。Windows 11 x64，本机 D3D11 / Media Foundation。仅使用合成测试视频、音频和鼠标事件，不采集用户屏幕或输入内容。

## 实现

- 声音/缩略图独立会话、任务与取消；画面编辑不打断混音，声音编辑不重启缩略图。音频准备失败后再次点击播放可重试。
- SHA-256 内容标识包含媒体元数据、源文件大小/修改时间及相关参数，不使用 revision 作为内容身份。撤销/重开可复用混音、波形和图片。文件先写临时路径再替换，明显损坏的 WAV/PNG 按缓存未命中重建。
- 缓存按最近使用时间、1 GiB 目标和 400 文件上限回收，只处理 `editor-*`，保护正在读写/播放的混音。活动混音可以超过目标；没有承诺严格限制正在进行的单个媒体任务。
- 时间线按可见格子的不同输出时间取帧，最大请求队列 128、内存图片 160，渲染最长 384×216。滚动/缩放替换排队需求，旧图仅在同片段回退。
- 光标支持 recorded/arrow/dot/crosshair，none/ripple/highlight 点击反馈及颜色/大小/不透明度；静止隐藏和 ±2 秒校准。旧工程默认为原有外观。点击与隐藏由源时间确定，不依赖 UI 播放帧率。
- 命名预设库支持保存、重命名、删除、默认选择及跨工程应用。背景进入库后再复制回目标工程；剪辑、时序效果和片段音量保留。应用可撤销；录制默认只在新工程首个检查点应用，续录保留。

## 自动验证

完整 Release 构建 0 警告 / 0 错误，7 组 CTest、201 项默认托管测试通过（App 22 / Capture 6 / Domain 111 / Workspace 62，硬件测试排除）。日志：`artifacts/editor-polish-final-build.log`。

专项命令：

```powershell
.\scripts\build.cmd -Configuration Release -LockedRestore
.\scripts\validate-editor-polish.cmd -OutputDirectory artifacts/validation/editor-polish/new-run
.\scripts\validate-m3.cmd -NativeBuildDirectory artifacts/native -OutputDirectory artifacts/validation/editor-polish/new-m3-run
```

专项最终完整证据：`artifacts/validation/editor-polish/run-06/ui`。

- 人为延迟声音准备时，可见缩略图继续生成；声音与图像取消路径独立。
- 画面编辑保持混音路径，音量修改生成新路径，撤销复用旧混音且不增加混音次数。
- 原生参数校验、旧工程默认值、静止渐隐的时间求值、内容标识失效与缓存容量/活动保护/损坏文件检查通过。
- 九组光标画面/成片独立解码对比通过；平均 RGB 误差约 2.07–2.20。静止隐藏 PNG 与完全隐藏光标参考逐字节像素一致，样式、点击和时间校准均造成可见差异。
- 同一场景缓存有 15 张不同时间/内容的胶片帧，避免仅靠文件数量将重复代表帧误报为连续胶片。
- 命名、重命名、默认选择/清除、损坏条目跳过、跨工程背景独立性、默认预设跨录制分段保留及应用撤销通过。
- 实际 WinUI ContentDialog 的保存、重命名和删除按钮通过 AutomationPeer 调用，普通浅色/紧凑深色布局测量与截图通过。最后的蓝色按钮样式单独在 `preset-ui-final` 复验。

原有 M3 的 28 组成片回归通过：`artifacts/validation/editor-polish/m3-regression/result/independent-media-verification.json`，最差 RGB 平均误差 2.673，最低 PSNR 28.88 dB。

## 验证边界与文档核对

- Windows Computer Use 两次刷新目标后均无法激活编辑器，最后错误为 `failed to activate captured window`。本轮 UI 证据来自真实 WinUI 控件调用/测量/渲染，不能称为物理鼠标命中验证。SwapChainPanel 不包含在 RenderTargetBitmap 控件截图中，视频像素由独立 PNG/MP4 检查覆盖。
- 首轮验证发现无缩略图时对空值类型集合调用 MinBy 的异常，已修复并复跑；帧对照改用严格相同的输出帧时刻，未放宽像素阈值。
- 通过 `gh run list` 核查基线 main 的 [Windows build and logic tests](https://github.com/lucasx001/frametrail/actions/runs/38923131939) 已成功，head 为 `0e788a3584691c919149a0d35aee8863e99be857`。本轮分支的云端 CI 需单独查看，不能用该基线结果代替。
- 更新 README/spec/progress/user-guide/schema/release-notes、0007 过时 M3 描述和 0014 已验证子项；补齐 0015–0018 与 [当前待办](../todo.md)。早期验证页加历史快照说明，保留原始执行记录。
- 多小时准备/滚动与缓存峰值、跨 GPU/DPI、真机 Android、正式签名和干净机器安装仍待验收。完整 PCM 混音仍受 RIFF 4 GiB 限制。

本轮实现随 `codex/editor-media-polish` 分支提交；没有生成新的发行包。
