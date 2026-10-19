# P2 工作流、性能与易用性验证

日期：2026-10-18；2026-10-19 补充首次启动修复（Asia/Singapore）。基于已合入 P1 的 `eb7ee2b`，分支 `codex/p2-workflow-performance`。本轮没有安装软件、发布 Release 或操作 Store 账户。

对应 [Review](../reviews/2026-10-18-dogsc-review.md)、[0025](../0025-timeline-workflow.md)、[0026](../0026-browsing-performance.md)、[0027](../0027-export-gpu-surfaces.md)、[0028](../0028-update-sources.md)、[0029](../0029-language-onboarding-accessibility.md)。

## 实现与自动检查

- 静音悬停保留原 playhead/播放状态，离开恢复；点击确定 seek。UI 线程请求队列只保留一个在途请求和一个替换，原生 generation 拒绝过期画面；编辑/关闭/播放会取消浏览请求，旧几何结果不能重新显示悬停中的选择框。
- 同类效果仅粘贴属性，保留目标 ID、起止时间、文字/图片、源轨绑定及录制焦点轨。工作区同一保存/撤销路径负责提交。六类转移、兼容拒绝及一次撤销/重做测试通过。
- 时间线按 Dispatcher 周期合并重绘，复用可见片段/效果边框、压缩过密视觉；效果区间索引用于浏览/命中，命中保留原效果顺序。项目列表使用数据项、模板和回收容器，搜索 150ms 节流。
- 678 条中英文嵌入资源，检查完整 key、格式参数与源码 lookup；语言偏好原子保存，下次启动生效，不翻译工程/备忘录/创作文字。英文帮助文档随包分发。首次使用和设备错误/输入菜单提供系统设置深链，不自动改权限。
- 时间线公开 Selection、RangeValue、SelectionItem 和 Invoke 语义、可见项边界及选中项；支持焦点和片段/效果键盘导航、定位、边缘调整、重排。系统减少动画偏好继续用于应用动画，不改变视频创作效果。

```powershell
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
```

`artifacts/p2-build-final-05.log`：Release 0 警告/0 错误；7 组原生测试、238 项默认托管测试通过（App 49、Capture 6、Domain 111、Workspace 72）。Audio 硬件项目没有默认匹配用例，不计入通过数量。

## 实际应用与性能

### 2026-10-19 首次启动回归

用户报告普通启动后只见“选择屏幕、窗口或区域…”文本，无法继续。现场 UI Automation 确认首次使用 ContentDialog 存在，但挂载到 462×56 DIP 录制胶囊的 XamlRoot，标题和操作区被裁切。此前带 `--editor` 的验证跳过普通启动，且只在较大的设置窗口显示引导，因而漏掉这个问题。

说明现使用独立的有标题栏窗口，归属来源窗口并保持在胶囊上方；正文滚动、操作区固定，支持开始使用/关闭/Esc。窗口复用防止重复打开，完成后写入首次使用状态；来源窗口退出会关闭引导而不写入完成标记。胶囊自身不变形，也不被模态层禁用。

`artifacts/onboarding-build-01.log`、`onboarding-build-final.log`：Release 0 警告/0 错误。`artifacts/onboarding-app-tests-with-native.log`：App 49 项通过；最初直接运行测试时缺少原生 DLL 搜索路径，失败日志保留在 `onboarding-app-tests.log`，加上本地原生输出路径后复验通过。

`artifacts/validation/onboarding/zh-01/onboarding-validation.json` 和 `en-01/onboarding-validation.json`：每种语言 10 项检查通过，覆盖独立 XamlRoot、56 DIP 胶囊保持可用、展示前不写状态、重复请求共用窗口、操作按钮完成、再次启动跳过、强制重开、窗口关闭和来源退出。正常尺寸及 400×360 DIP 外框的紧凑窗口截图/按钮边界检查通过；正文可滚动，按钮始终位于可见区域。测试使用输出目录内独立偏好文件，不重置用户偏好。

另在真正的无参数首次启动中通过 Computer Use 观察独立窗口、点击“开始使用”，确认完成标记落盘，胶囊菜单可以打开；通过设置 → 录制重新打开说明后，关闭来源设置窗口也没有残留引导。最终通过 `scripts/run.cmd -Configuration Release` 再次无参数启动，UIA 确认直接进入有来源/设备/开始按钮的录制条，没有引导弹层，并保持应用运行供继续使用。实际走查摘要见 `artifacts/validation/onboarding/desktop-walkthrough.json`。应用内布局验证与实际普通启动分别记录，不再以设置弹窗截图替代首次启动。

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\toolchain\dotnet"
.\src\FrameTrail.App\bin\x64\Release\net8.0-windows10.0.26100.0\win-x64\FrameTrail.App.exe --onboarding-self-test "$PWD\artifacts\validation\onboarding\新目录" --language zh-Hans
```

### P2 编辑器与项目列表

`artifacts/validation/p2/final06-en/`、`final06-zh/`：最终自包含候选实际运行，两种语言的 `p2-ui.json` 均通过。覆盖 100 次快速悬停、暂停/播放恢复、点击提交、不改修订、UIA 初始片段选择/定位、键盘焦点、设置五页、首次使用弹窗及官方更新检查。保留窗口截图。切换检查器时同步时间线的可访问选择，避免后台效果检查器的默认选择覆盖片段语义。

4 小时逻辑时间线（合成的 resolved 数据，非 4 小时真实录制）：2400 片段/12000 效果，0.1/8/80/400 px/s 共 48 次滚动/缩放，最多约 1059 个可见元素。最终两种语言的整个压力阶段约 3.53/3.66 秒，步骤 P95 约 159/163ms；步骤含固定 20ms 等待和布局处理，不是输入到显示延迟的纯测量。进程私有内存快照约 454/451 MiB，包括 4K 预览和运行时；不代表缓冲大小或长期泄漏结论。较早轮为 2.39/2.45 秒，保留开发机负载波动，不将最短轮当作保证。

`artifacts/validation/p2/verified-library/p2-library.json`：1000 份真实磁盘元数据，最多 10 行容器；6 次远距离滚动步骤约 59–92ms（含固定 45ms 等待），快速搜索保留最终结果；私有内存快照约 164 MiB。截图确认行内容与分离的打开/删除按钮可见。开发输出轮 `library-final` 最多 9 行。原首次压力入口只数容器，未发现空白模板；已由原项目列表回归发现并修正为 `ContentTemplateRoot` 挂载，并增加实际行尺寸断言。失败轮不算通过证据。

`library-regression-02/project-library-validation.json`：深色/紧凑浅色、名称搜索、打开与保存切换、取消删除、独占拒绝、活动/非活动/损坏测试工程的回收站删除均通过。只处理本轮 `artifacts` 下可丢弃工程。

`p1-regression-final/p1-validation.json`：画布 mask/focus/sticker 移动、一次撤销/重做与像素对照，倾斜/旋转命中，fit/125%/200% 预览倍率、裁切、流式播放、全屏及保存重开通过。预览倍率测试不替代 Windows DPI 矩阵。

Computer Use 在实际英文窗口核对布局、播放、Home、F6 和时间线效果导航；发现并修正了英文背景标签截断、属性前缀空格与焦点恢复问题。人工 Narrator 全流程仍单列。

## GPU 导出

NV12 转换写入 MF allocator 管理的 D3D11 表面，最多 16 个 sample，编码器释放后才能复用。非 D3D 编码器走 CPU；D3D 类型拒绝和样本池停滞可安全重试 CPU，取消不重试。兼容路径复用 CPU 转换数组，保留最终校验和原目标保护。统计包含 GPU/CPU 帧数、回读、写入和池等待。

`export-01/p2-export.json`、`independent-decode.json`：相同 2 秒带效果素材，H.264 的 8 份输出完整独立解码，60/120 帧和时长正确；四对 GPU/CPU 解码像素完全相同（PSNR=inf）。自动路径每帧为 GPU sample，CPU readback 时间为 0。

| 输出 | CPU 路径耗时 | 自动 GPU 耗时 | CPU 路径进程 CPU 时间 | GPU 路径进程 CPU 时间 |
| --- | ---: | ---: | ---: | ---: |
| 1080p30 | 2077ms | 562ms | 2078ms | 719ms |
| 1080p60 | 2042ms | 706ms | 3016ms | 1141ms |
| 4K30 | 2240ms | 835ms | 3234ms | 1078ms |
| 4K60 | 2736ms | 1175ms | 3531ms | 1656ms |

这是顺序运行的单机短测，包含初始化、缓存和驱动负载，不能推断所有工程的加速比。GPU sample 池有内存成本：进程私有内存采样峰值约 CPU 245–561 MiB、GPU 319–628 MiB。转换统计是 CPU 提交计时，不冒充 GPU 利用率/执行时间。

`codecs-final/export-matrix.json`：本机 H.264/HEVC/AV1 × 1080p/1440p/4K × 30/60 FPS 共 18 份 0.5 秒视频，均实际使用 GPU samples；3 组进行中取消/目标占用保护通过。`independent-decode.json` 对 18 份视频全片独立解码并核对帧数。强制 CPU 路径在前述 8 份对照中实际运行；真正不兼容显卡/驱动及 GPU 故障注入仍需扩展环境。

```powershell
$env:PATH = "$PWD\artifacts\native-release\bin;$env:PATH"
.\.tools\toolchain\dotnet\dotnet.exe run --project tests/FrameTrail.Workspace.Smoke -c Release -- --p2-export <测试工程> <新输出目录>
.\.tools\toolchain\dotnet\dotnet.exe run --project tests/FrameTrail.Workspace.Smoke -c Release -- --video-codec-matrix <测试工程> <新输出目录>
```

## 更新与分发

MSIX/Store 保留包更新来源和 Store 深链；站外版查询官方 GitHub 稳定版本、说明与实际附件。匿名 API 本机出现限流，官方稳定版重定向已确认 v0.0.2；双语 `update-check.json` 均 `confirmedOnline=true`。TLS 失败轮明确显示未完成并保留官方入口，没有关闭证书验证，也未把失败当作没有更新。协议测试覆盖版本比较、无版本/限流/错误、预发行/非官方 URL 拒绝和回退重定向。

2026-10-18 归档候选为 `artifacts/packages/p2-final-06/`。`artifacts/p2-package-final-06.log`、`p2-package-final-06-verify.log`：600 个载荷文件、ZIP/MSIX 条目及 SHA-256 全部通过；自包含启动/引擎生命周期 smoke 通过。当时的双语/P1 复验直接使用该候选；列表模板与 1000 项目已在同一修正后的实现中复验。此归档包不含 2026-10-19 的首次启动修复；修复后的 Release 开发输出使用 `scripts/run.cmd -Configuration Release` 启动，回归证据单独列于上方。只在本机编译/运行，不代表干净机器安装升级或 Store 认证。

实现依据：[MF 视频样本分配器](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mfcreatevideosampleallocatorex)、[WinUI 虚拟列表](https://learn.microsoft.com/en-us/windows/apps/develop/performance/optimize-gridview-and-listview)、[GitHub Releases API](https://docs.github.com/en/rest/releases/releases)。

## 保留的扩展验收

Windows 125%/150%/200% DPI、跨屏负坐标/捕获排除、跨 GPU/编码驱动、不同外设/采样率、复杂语音音乐变速听测、真实多小时编辑与句柄长期趋势、锁屏/睡眠/GPU 丢失、Android 真机、联合多轨同步、Narrator、干净安装升级/卸载、实际 Store/MSIX 更新源及认证仍需对应环境。离线导出继续使用整片临时 PCM；更低磁盘峰值的流式音频导出与 P3 光标/预设包扩展留在待办。
