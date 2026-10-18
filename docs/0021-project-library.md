# 0021 — 应用内项目列表

日期：2026-10-18。Windows 11 x64 / WinUI 3。

## 目的与交互

打开已有项目时直接展示项目列表，点击项目行进入编辑器；独立的行内删除按钮不会触发打开。交互参考 OpenScreen 的 [OpenProjectModal 测试](https://github.com/getopenscreen/openscreen/blob/main/src/components/ai-edition/OpenProjectModal.test.tsx)：搜索、选中项目、独立删除与确认 / 取消。FrameTrail 删除沿用项目包与素材整体回收的语义，确认框明确展示范围和完整路径。

- 准备胶囊的文件夹按钮 / 更多菜单与编辑器的「切换项目…」使用同一原生列表窗口。
- 展示名称、录制时长、画布尺寸、最近保存时间和路径，标记当前工程；按保存时间从新到旧排序。录制时长来自 `sourceDuration`，不冒充剪辑后的成片时长。
- 支持名称 / 路径搜索、单一可打开搜索结果按 Enter 打开、Esc 关闭、刷新、空列表和无匹配状态。
- 自动列出录制保存目录与当前项目父目录，保留最近打开的项目及添加的其他位置。默认不弹系统文件选择器；「其他位置…」只负责添加一个目录或项目包，然后回到列表。
- 每行删除先展示确认，默认取消；删除成功刷新列表。损坏 / 未完成工程可清理，外部会话占用会提示错误并保留工程。当前项目删除先保存和关闭；删除其他工程不打断当前编辑器。

## 实现

`ProjectCatalog` 在后台仅读元数据，不获取写租约、不创建工程内缓存、不解码媒体；直接枚举 `.frametrailproject`，不递归普通目录，排除经过符号链接 / 目录联接的路径。偏好保存在当前 Windows 用户的 `FrameTrail/project-catalog.json`，原子替换，最多保留 100 个已打开项目与 20 个额外位置。偏好损坏、已移走的项目和不存在的位置不阻止其他列表项显示。

`ProjectLibraryWindow` 复用原生主题与编辑器样式，项目行与删除按钮为相邻控件，删除操作固定捕获对应目录。加载 / 操作中禁止重复操作；错误留在列表内显示。打开和删除经由 `MainWindow` 复用既有项目切换、输入保存、首帧验证、写入屏障、资源释放、独占租约及 Windows 回收站路径。新打开的工程预览仍默认 4K。

## 验证

- `scripts/build.cmd -Configuration Release`：应用 0 警告 / 0 错误，7 组原生及 209 项默认托管测试通过（App 30、Capture 6、Domain 111、Workspace 62；真实音频硬件测试不在默认集合）。新增 8 项目录 / 元数据 / 偏好测试覆盖去重、排序、时间基准、损坏 / 未完成工程、缺失位置、占用时列出和取消扫描。
- `FrameTrail.App.exe --project-library-self-test <新的输出目录> <有效测试工程> --inspect-ui`：只复制可丢弃工程，实际回收自己创建的副本，不操作用户项目。
- `artifacts/validation/project-library-20261018/run-03/project-library-validation.json` 为 `success: true`：真实 WinUI 列表 / 删除确认按钮、取消、列表点击打开、切换保存未提交数字输入、释放旧租约、外部占用拒绝、非活动 / 损坏 / 活动工程回收与删除后刷新全部通过。最终版本补充损坏项目行的正常文字对比度，以及目录在扫描中被移走时忽略该行。
- `projects-dark.png` / `projects-compact-light.png` 验证深色与 520 DIP 紧凑浅色布局，打开和删除按钮不重叠；`projects-delete-confirm.png` 记录包含完整路径的确认框。已检查应用生成的实际布局图片。
- 初轮 `run-01` 在设置搜索框文本后立即断言，早于 WinUI `TextChanged` 调度。等待实际文本事件完成后重新执行，`run-02` 的搜索和空状态通过。单独运行 App 测试须将 `artifacts/native/bin` 加入当前进程 PATH，或使用标准 `scripts/test.cmd`。
- Computer Use 可枚举新版「FrameTrail · 项目」窗口，但绑定时仍将开发版归到旧安装版路径，重试报 `window id 19925900 no longer belongs to ...; current owner is ...`。因此额外的外部鼠标自动化没有完成；上面的打开 / 删除交互由应用内真实 WinUI 控件的 Invoke 模式、文件系统与回收站结果验证。

该轮验证不覆盖大量工程 / 网络盘的长期交互性能、跨显示器 DPI 或正式安装包升级。未替换用户安装版或发布新版本。
