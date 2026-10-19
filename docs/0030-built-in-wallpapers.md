# 0030 · 内置桌面壁纸

目标：让用户像 OpenScreen 一样直接选择桌面壁纸作为录屏画面外的背景。

- [x] 从 OpenScreen 原仓库固定提交导入全部 18 张 JPEG，保留原图、来源、尺寸、SHA-256 与上游 MIT 声明。
- [x] 在「画面 → 背景 → 壁纸」提供三列缩略图库、选中边框和自定义图片导入，适配中英文、明暗与紧凑窗口。
- [x] 点击后复制原图到工程，内容相同的壁纸复用文件；撤销 / 重做、保存 / 重开、视觉预设和导出沿用已有图片背景数据结构。
- [x] 原生图片接收最大单边 8192；超过 16777216 像素时按比例缩小解码，单张 GPU 位图不超过 64 MiB。缩略图解码宽度限制为 240。
- [x] 增加受控工程的 18 图预览、主题 / 紧凑布局、可访问选中状态和 MP4 导出验收入口，以及素材复用、源文件独立性与失败清理测试。

实现入口：`EditorWindow.Wallpapers.cs`、`BuiltInWallpapers.cs`、`EditorVisuals.cs`、`media/image_decode.hpp`。工程 schema 无新增字段；仍保存 `editor.backdrop.kind=image` 和工程内 `assets/` 相对路径。

验证命令：`scripts/validate-wallpapers.cmd`（先执行 Release 构建）。结果与验证边界见 [壁纸验证记录](validation/built-in-wallpapers.md)。
