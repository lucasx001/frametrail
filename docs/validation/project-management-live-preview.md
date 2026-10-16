# 本地项目管理与编辑预览更新

日期：2026-10-16。环境：Windows 11 x64 / build 26300，本机 D3D11 / Media Foundation。

## 用户入口

- 准备胶囊增加文件夹按钮「打开已有项目」，更多菜单保留打开入口。选择 `.frametrailproject` 文件夹，不要求用户查找内部 JSON；初始位置固定为配置的项目保存目录。
- 保存目录写入当前 Windows 用户的 FrameTrail 设置；编辑器「项目 → 切换项目…」从当前项目父目录开始。原生 Common Item Dialog 的 `SetFolder` 设置实际文件夹，避免被上次无关的浏览历史覆盖。
- 切换先保存输入和编辑，预先验证新工程与首帧。旧项目在成功前保留；旧预览、缓存任务和可写租约完成释放后，新窗口沿用旧窗口位置/尺寸。相同项目不重开，取消选择不切换。无效项目、占用和保存失败不丢失当前会话。
- 删除入口仅保留在编辑器右上角的独立「删除项目」按钮。应用内确认框展示名称及完整路径，默认「取消」保留当前项目；点击「确认删除」后等待保存并释放文件占用，删除成功才返回胶囊。Windows `FOF_ALLOWUNDO` 优先回收；`FOF_WANTNUKEWARNING` 保留无法回收时的额外警告，用户可取消。不会自动永久删除而跳过系统警告。
- 录制、导出和项目切换期间禁止相应操作。删除失败时报告错误，当前项目仍存在时尝试重新打开。

## 预览行为

- 新增 `ft_media_update_project`：接受同工程、同源资产的快照，校验身份、路径/格式/偏移/源时长和 generation，预先构建时间映射与场景；不更换已有解码器、GPU 或 swap chain。输出时长使用原子值同步给状态查询，完整更新使画面设置缓存失效。
- 分割、剪辑、画面裁切、运镜、边框、画布尺寸及撤销/重做保留当前渲染会话；渲染完成后显示新帧。页面控件、选中项和播放头保持，尽量映射到相同源时间。
- 缩略图保留旧图或邻近缓存，后台生成新图后逐项替换。仅画面变化更新缩略图，仅剪辑/音频设置变化重新准备声音。项目切换、首次加载、手动重新加载仍走完整资源创建。

## 自动验证

- Release 0 警告 / 0 错误；`scripts/test.cmd -Configuration Release -LockedRestore` 通过 7 组原生和 156 项默认托管测试，包含 8 项新增保存位置/项目路径测试，以及损坏工程清理时的独占租约测试。
- `--project-open-self-test <输出目录> <有效项目夹>` 使用复制的真实视频工程：验证原生对话框实际初始目录、胶囊打开、A/B 项目切换、尚未提交的数字输入保存、相同项目不重开、无效/外部占用工程保留当前窗口、保存文件锁定时阻止切换/删除及解锁重试、返回项目首帧预览。
- 对测试项目执行真实 Windows 回收站操作：活动项目删除后编辑器/租约关闭并返回胶囊；非活动项目占用时拒绝，释放后回收成功；损坏工程也可清理，不要求先解码素材；父目录和旁边的无效测试目录保留。未删除用户已有项目。
- 8 组编辑（分割、剪辑、画面裁切、浏览器边框、画布尺寸、缩放、撤销、重做）验证同一 `MediaSession` 与同一 `IDXGISwapChain` 指针、实时输出时长、拒绝过期与跨项目更新。分别保存热更新和全新会话的 GPU 帧，由 FFmpeg 独立解码比较，8 组平均/最大 RGB 误差均为 0。

- 热更新后的当前媒体会话直接导出编辑结果，独立 FFmpeg 验证 84 帧 / 2.8 秒且完整解码；导出设置更新不重建预览。胶囊 UI 回归验证 462 × 56 DIP 布局、开关、来源选择和真实 WGC 暂停续录/重开。

主要证据：`artifacts/project-final-build.log`、`artifacts/project-open/ui-04/project-open-validation.json`、`artifacts/project-open/ui-04/independent-preview-verification.json`、`artifacts/project-open/recorder-ui/ui-validation.json`。此前 `ui-02`、`ui-03` 的项目和预览验证也已通过。早期 `ui-01` 的数字输入用例未选择自定义尺寸，测试未改变宽度；修正用例后重新验证，保留原记录。

复验：运行应用 `--project-open-self-test <新的输出目录> <可读的现有项目目录>`，结束后运行 `python tests/FrameTrail.Workspace.Smoke/verify_live_project.py <输出目录>`。只使用可复制的测试工程；自测会回收自己创建的两个副本及一个损坏样本。

## 删除交互调整复验

2026-10-16 根据反馈将删除入口移到编辑器右上角，并用 WinUI `ContentDialog` 取代原生消息框。胶囊与项目下拉菜单不再提供删除入口。

- `artifacts/delete-ux-build.log`：Release 应用及依赖构建通过，0 警告 / 0 错误。
- `artifacts/delete-ux/ui-01/project-open-validation.json`：全部通过。实际布局检查右上角删除/导出按钮在普通深色和 840 × 600 DIP 浅色窗口内完整且不重叠；通过 WinUI 按钮的 Invoke 模式执行「取消」与「确认删除」，取消保留项目和编辑器，确认关闭编辑器并将测试副本移到回收站，主窗口可见且处于准备胶囊状态。
- 截图：同目录 `delete-toolbar-dark.png`、`delete-toolbar-compact-light.png`、`delete-confirm-dark.png`、`delete-confirm-compact-light.png`、`delete-return-capsule.png`。已检查布局及确认框内容；UI 位图不能捕获 swap chain 视频层，视频正确性由独立 GPU 帧比较验证。
- 同轮项目切换、输入保存、文件占用/保存失败保护回归通过。`independent-preview-verification.json` 的 8 组画面比较平均/最大误差均为 0；MP4 完整解码 84 帧 / 2.8 秒。此次回收的仅为自测创建的副本。

## 边界与依据

自测通过真实 WinUI 控件、原生选择器状态、媒体/文件系统及回收站执行；未自动操作选择器的鼠标导航和系统永久删除确认框。预览资源复用及画面正确性已验证，不将其等同于长工程 / 多 GPU / 4K / 复杂音轨的交互帧率验收。

原生选择器的目录固定方式参照 [Microsoft WindowsAppSDK picker 规范](https://github.com/microsoft/WindowsAppSDK/blob/main/specs/Storage.Pickers/Microsoft.Windows.Storage.Pickers.md)；回收与永久删除警告参照 [Microsoft SHFILEOPSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shfileopstructw)。本项目保持现有 Windows App SDK 版本，直接使用系统 Common Item Dialog。
