# 内置壁纸验证 · 2026-10-19

任务：[0030](../0030-built-in-wallpapers.md)。所有媒体验证均使用脚本生成的两秒测试图工程，没有修改用户工程。

## 构建与自动测试

- `scripts/build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore`：Release 0 警告 / 0 错误。
- 8 组原生测试通过，包含新增的图片输入尺寸、比例缩放和 16777216 像素解码预算检查。
- 241 项默认托管测试通过：App 49、Capture 6、Domain 111、Workspace 75。新增测试覆盖同图复用、撤销 / 重做、删除导入源后重开并探测工程图片、失败清理，以及复用损坏资源时保留已有文件。硬件分类未执行。

## 应用与独立媒体结果

`scripts/validate-wallpapers.cmd -OutputDirectory <新目录> [-Language en]` 会生成工程、启动当前 Release 应用、运行验收并独立比对图像。最终结果：

- 中文：`artifacts/validation/wallpapers/final-zh/result/wallpapers-ui.json`，`success=true`。
- 英文：`artifacts/validation/wallpapers/final-en/result/wallpapers-ui.json`，`success=true`。
- 每次均导入并原生渲染全部 18 张壁纸，校验上游原图 SHA-256、工程路径和唯一选中状态。
- 图库浏览不修改工程；重复选择不新增素材；纯色切换、撤销 / 重做恢复壁纸与选中边框。
- 中英文、明暗和 840×600 DIP 紧凑窗口出图，Toggle 可访问状态正确；所选缩略图统一使用应用蓝色，避免系统强调色覆盖。
- 生成可解码的 H.264 MP4，1920×1080、两秒、30 FPS。
- `wallpaper-pixels.json` 从画面外侧八处采样，独立对照居中裁切的上游 JPEG；18 份预览和导出首帧全部通过。单通道均值容差为 12/255，导出实测约 1.57/255。这能发现未应用壁纸、错误图片及陈旧画面，而不只依赖工程 JSON。

控件截图保留为 `gallery-light.png`、`gallery-dark.png` 和 `gallery-compact.png`；WinUI `RenderTargetBitmap` 不包含 SwapChainPanel 的视频层，因此截图中的大预览区为黑色。实际原生画面证据为 `wallpaper-1.png` 至 `wallpaper-18.png`、`wallpaper-export.mp4` 和解码后的 `export-first-frame.png`。

## 发布输出与来源

`dotnet publish --no-build --no-restore -c Release -p:Platform=x64 -p:NativeBuildDirectory=<native-release> -o artifacts/wallpapers-publish` 通过。输出包含 18 张 JPEG、catalog.json 及 `Licenses/openscreen-MIT.txt`；全部 JPEG 的 SHA-256 与源清单一致。未安装或更新已有安装版。

上游为 `siddharthvaddem/openscreen`，固定提交 `ca8104c279da0d4b4481ec992ff103949e22c772` 的 `public/wallpapers`。18 张文件原样保留，总计 24837535 字节。来源与许可见 [第三方声明](../../THIRD_PARTY_NOTICES.md)。

## 验证边界

Windows computer-use 能列出新编辑器窗口，但重新获取窗口时两次返回 `window id ... no longer belongs to ...; current owner is ...`，两侧显示相同安装路径，因此停止该入口的指针操作。控件流程、布局和可访问状态由真实 WinUI 应用内验收执行；本次没有把物理鼠标命中、Narrator 或跨 DPI / GPU 人工验收写成通过。现有安装版未关闭或替换。
