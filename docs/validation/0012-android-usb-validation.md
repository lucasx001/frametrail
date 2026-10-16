# M5 / 0012 Android USB 实现与验证

日期：2026-10-16，Asia/Singapore。分支：`codex/m5-android-usb`，基于 `main` 的 `822c592`。

结论：Android USB 的本地实现和无需手机的自动验证已完成，状态为**待验收**。用户明确说明暂时没有手机，先完成实现与自动验证。本次没有手机 USB 实录、Android 内部音频实录、30 分钟跨设备同步或干净 Windows 安装证据；M4 的未完成验收保持原状态。提交记录以 Git 历史为准，尚未发布。

## 已实现

| 链路 | 实现与边界 |
|---|---|
| USB 发现 | `AndroidBridge.cs` 使用固定 ADB 与本机共享 daemon 的 `host:track-devices-proto-binary`；依据 protobuf 的 USB transport 类型筛选，保留多设备 transport ID 与授权/离线状态。Windows ADB native USB 后端可能没有 bus path，因此不把 `usb:` 文本当作唯一凭据。无线、mDNS 和模拟器被排除。 |
| 生命周期 | 每次连接使用随机 scid、私有远端 jar、动态分配的单个 forward 以及独立 adb shell 进程；取消/停止只清理本会话资源。没有 `adb kill-server`、自动驱动安装、提权或手机调试设置修改。普通设备显示和 `ToString()` 仅使用型号与散列标识。 |
| 原生接收 | `FrameTrail.Android.dll` 接收独立媒体 socket，解析大端 12-byte 包头、配置/关键帧标记与设备 PTS。每包最多 16 MiB，配置最多 64 KiB，PCM 每包最多 4096 bytes；拒绝错误格式、逆序 PTS、超时和配置变化。 |
| 视频与预览 | 固定 scrcpy server / 协议 3.3.4，H.264、8Mbps、最大边 1920、30/60 FPS 上限、无 B 帧与一秒关键帧间隔。MF 直接封装 H.264 为 MP4；MF 低延迟解码到原生 NV12，D3D11 转换和 SwapChainPanel 呈现。像素不跨入 C# 数组。编码配置变化结束会话；本轮没有自动重编码兼容回退。 |
| 方向 | `capture_orientation=@` 锁定初始采集方向；工程保存固定尺寸。横屏 320×240 和竖屏 360×800 样本均验证。实际旋转/折叠/ROM 行为仍待真机验收。 |
| 声音 | Android 内部音频协商 raw PCM16 / 48kHz / 双声道，按设备 PTS 裁剪并补齐有界间隙，保存独立 `androidAudio` WAV。可选择必选音轨或无声降级；首帧和必选音轨就绪才开始。预览不播放手机声音。电脑麦克风复用 WASAPI 独立 WAV；首轮禁用同时采集 Windows loopback。 |
| 检查点 | 提炼运行结束接口并复用 RecordingCoordinator。连续排空同一手机流，最多两个 MP4 writer；下一 writer 等待关键帧，以其媒体时间/QPC 边界 finalize 前段。暂停时关闭 writer、继续排空，恢复等待新关键帧；标记仍可用。 |
| 工程与编辑 | Android 使用 schema 2 的 `primarySource` 与 `androidAudio`；旧桌面 schema 1 继续严格读写，支持显式升级。C++ 命令往返保留新字段。断开对端后仍能重开、剪辑、变速/撤销、套用通用效果、混音、预览、导出 MP4/M4A 与完整素材副本。 |
| 输入语义 | Android 工程拒绝 Windows pointerEvents 与 systemAudio；不会把桌面鼠标当成手机触摸。自动点击运镜在 Android 工程中不生成效果；手动运镜和通用效果可用。 |
| 界面 | 来源轮盘第四项「手机」与更多菜单的 Android USB 入口；选择手机、刷新、授权/驱动指引、连接预览、内部音频/降级选项、开始和取消。电脑麦克风使用录制条选项；手机模式下桌面摄像头/光标不生效。 |
| 分发 | `setup-android.cmd` 获取并校验固定组件，构建复制到 `android-runtime/`。`package-android.cmd` 发布自带 .NET/Windows App SDK 的便携测试包，复制原生 DLL、WinUI PRI/XBF 与独立许可；没有制作/发布安装器。 |

## 构建和默认测试

Windows 11 build 26300 x64；本地工具链 .NET SDK 8.0.426、MSVC v143、SDK 26100。命令：

```powershell
.\scripts\setup-android.cmd
.\scripts\build.cmd -Configuration Release -NativeBuildDirectory "$PWD\artifacts\native-release" -LockedRestore
.\scripts\validate-m5.cmd
.\scripts\package-android.cmd
```

Release 构建 0 警告、0 错误。7 组原生测试通过。默认托管测试共 131 项：App 6、Capture 6、Domain 79、Workspace 40；需要真实音频设备的 Hardware 类别未运行。覆盖新协议分配边界、USB 类型/多设备/授权状态、脱敏、不可变版本信任锚、无效录制计划、schema 1/2、来源尺寸/轨道隔离、迁移及原生命令往返。

恢复压力测试复现一次 Windows `File.Replace` 的短暂占用失败。修复为保留已经 flush 的同一临时文件，对 sharing/lock/无法删除被替换文件错误最多重试五次（总退避 750ms），持续失败仍上报；不改成截断覆盖目标。新增真实 `FileShare.Read` 锁竞争测试验证重试成功，当前与 previous 修订均完整。

证据：`artifacts/m5-final-build.log`、`artifacts/m5-app-build.log`、`artifacts/m5-native-build.log`、`artifacts/test-results/FrameTrail.*.Tests.trx`。云端 CI 本轮未运行；workflow 已增加固定 Android 组件准备步骤。

## 真实编码样本与模拟协议对端

最终运行：`artifacts/validation/m5/run-20261016-105236/`。

测试对端是 C# TCP peer，按所固定版本发送真实 H.264 access unit、关键帧/配置、设备 PTS 与 PCM；FFmpeg 仅负责开发测试的样本生成和独立核查，不参与产品运行。到达节奏故意加入抖动，PTS 仍保持 30Hz；样本剥离每个 access unit 内的 SPS/PPS，仅通过独立配置包发送，验证 MediaCodec 常见的配置 / 画面分离路径。解码器和每个新分段显式带入配置后才消费关键帧。

- `android-validation.json`：固定运行时与真实本机 ADB 枚举执行成功，实体 USB 设备数为 0；持续流检查点、暂停/继续、两枚标记、独立手机音轨、横/竖屏、缺音轨策略、断线/超时/配置变化/大包、途中音频消失、握手取消及连续独立会话均通过。
- `independent-media-validation.json`：17 个有效 MP4 的 H.264 尺寸、零点关键帧、递增 PTS 与完整解码均通过；录制分段 PTS 不受包到达抖动改变。进程终止时的 pending MP4 明确排除。
- `phone-live.png`、`portrait-live.png`：来自**实时解码 / GPU 预览纹理**的原生回读，已查看；独立像素检查确认真实彩色动态图案，不接受全黑纹理作为预览成功。另有编辑后的 `phone-preview.png`。
- 编辑和混音在模拟手机与 native capture 都已关闭后执行。加入独立合成麦克风轨后，独立频谱核查同时存在 440Hz 手机声与 880Hz 麦克风贡献，MP4/M4A 中均有 AAC 音轨；这不是电脑麦克风硬件实录证据。
- `process-crash/phone.frametrailproject.recovery.result.json`：实际终止新建的测试进程，恢复一个已完成检查点的主视频和手机音频，排除未完成的后一段；再次重开成功。

本轮像素验证修复了预览目标区域未显式设置造成的黑屏：明确视频处理器的源/目标矩形与颜色范围，先转换到独立 BGRA 纹理再复制到 swap chain。启用 MF 的低延迟解码选项后，受控样本在首个视频包即可产出可回读画面。该结果不等于真实 USB 手机延迟测量。

## WinUI 与桌面回归

- 手机页面：`artifacts/validation/m5/ui-20261016-103713/android-ui-validation.json`。浅/深色、无设备、未授权拦截均通过；查看了实际 WinUI RenderTargetBitmap 图像，布局完整。无手机时开始按钮禁用，未授权时给出手机侧授权提示。
- 桌面路径：`artifacts/validation/m5/desktop-20261016-103727/ui-validation.json`。374×56 DIP 胶囊、四种来源轮转、640×360 物理区域选择，以及真实 WGC 录制→标记→暂停→继续→停止→重开通过，保留两段与一枚标记。
- M2 回归：`artifacts/validation/m5/m2-regression-20261016-104013/`。共享预览/成片、seek、双轨混音、静音/增益与六档保持音调检查通过；独立比对的预览/成片 MAE≈2.25，双轨功率比≈1，0.25–4 倍速均保持 440Hz。

这些界面截图由应用实际渲染，并非操作系统鼠标/键盘自动化或跨 DPI 人工验收。

## 便携测试包

产物：`artifacts/packages/FrameTrail-M5-20261016.zip`。发布脚本显式复制原生引擎 DLL 和 WinUI 的 PRI/XBF 资源，并携带 .NET、Windows App SDK、固定 Android 组件及各自许可；包内保留本记录，说明尚未验收的设备与发布范围。

本机以仅含 `C:\Windows\System32` 的 PATH 和不存在的 DOTNET_ROOT 启动便携目录，并运行 Android 页面自动检查；已确认模块路径实际来自便携目录的 hostfxr / hostpolicy / coreclr，原生 DLL 与当前构建一致，页面检查通过。该检查用于确认包完整，不替代干净 Windows、OEM 驱动或安装器验收。最终检查证据：`artifacts/validation/m5/package-final-20261016/package-validation.json`。

## 固定依赖、来源与许可

| 组件 | 版本 / SHA-256 | 官方来源 |
|---|---|---|
| Platform-Tools 归档 | 36.0.0 / `12c2841f354e92a0eb2fd7bf6f0f9bf8538abce7bd6b060ac8349d6f6a61107c` | [Google 固定 Windows ZIP](https://dl.google.com/android/repository/platform-tools_r36.0.0-win.zip) |
| adb.exe | 1.0.41 / 36.0.0-13206524 / `1e1c2280b90b3f01ad84cd8df4858b1b1995012814f3ca8893bcc3ba3848edec` | 上述归档 |
| AdbWinApi.dll | `9a56e72fe1372cb722a80c00b79dcecf2b37165884e470ed05f00c668c0043b0` | 上述归档 |
| AdbWinUsbApi.dll | `5e77ccb2f25cd3a97553745adf1cd28a5fe8137cf64613b7fef9c6f92ff91f37` | 上述归档 |
| scrcpy server | 3.3.4 / `8588238c9a5a00aa542906b6ec7e6d5541d9ffb9b5d0f6e1bc0e365e2303079e` | [固定 server](https://github.com/Genymobile/scrcpy/releases/download/v3.3.4/scrcpy-server-v3.3.4)、[上游 SHA256SUMS](https://github.com/Genymobile/scrcpy/releases/download/v3.3.4/SHA256SUMS.txt) |

应用在执行工具前验证归档内运行文件的哈希与编译入代码的信任锚；单独改写 `sha256.json` 无法替换成其他版本。协议升级必须一起更新 server、解析器、测试与校验。

scrcpy server 的 Apache-2.0 许可保存在 `docs/licenses/scrcpy-Apache-2.0.txt`，随应用复制为 `android-runtime/scrcpy-LICENSE.txt`。Google 归档的完整 `NOTICE.txt`（含各组成部分的声明）复制为 `android-runtime/ADB-NOTICE.txt`，不能用 scrcpy 许可代替。产品包不引入 scrcpy 桌面端、SDL、FFmpeg 或 libusb；编解码使用 Windows MF。

技术依据：[固定版本 Streamer](https://github.com/Genymobile/scrcpy/blob/v3.3.4/server/src/main/java/com/genymobile/scrcpy/device/Streamer.java)、[固定 PCM 配置](https://github.com/Genymobile/scrcpy/blob/v3.3.4/server/src/main/java/com/genymobile/scrcpy/audio/AudioConfig.java)、[固定方向](https://github.com/Genymobile/scrcpy/blob/v3.3.4/doc/video.md#orientation)、[ADB USB transport protobuf](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/proto/adb_host.proto)、[MF H.264 解码器与低延迟属性](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-decoder)。

## 未完成的真机 / 发布验收

| 验收 | 当前证据 / 解除条件 |
|---|---|
| 实体 USB、多手机、授权撤销、离线、驱动/线缆 | protobuf 模拟与无设备引导通过；需真实手机和 USB 硬件验证，尚无认证型号 |
| Android 5–10 视频及 Android 11+ 内部音频 | 已按 API 检查和流实际能力处理；需不同系统/ROM 实录，特别是 Android 11 解锁状态 |
| 真实手机 + 电脑麦克风 | 代码接入现有 WASAPI、合成独立音轨混音通过；没有手机联合实录 |
| 30 分钟 ≤50ms 首尾同步 | 未执行。设备 PTS 驱动手机音视频；首帧建立 QPC 映射，到达偏移诊断含 USB 延迟，不能证明固定延迟已校准或漂移已消除 |
| 实际帧率、丢帧、长期资源 | 记录协商上限、收到包数、写出样本数和 PTS 节奏；需动态真机长测，不能把 30Hz 模拟源当作手机性能结果 |
| 旋转/折叠、受保护内容、服务崩溃/拔线 | 模拟配置变化/断流恢复通过；需真实手机验证，不能绕过受保护内容 |
| 干净机器与已有 Android Studio 共存 | 本机固定运行时、共享 ADB 类型枚举和便携构建路径已实现；仍需无开发工具机器及其他 ADB 工具并用验收 |
| 安装 / 更新 / 卸载及 M4 | 便携测试包不代表签名安装器或更新验收；0010/0011 的原验收未完成 |

不在首轮范围：无线连接、远程控制/剪贴板、动态旋转连续录制、Windows loopback 与手机音频同时捕获、任意编码器的自动重编码兼容。遇到非单调 H.264 PTS 或配置变化时安全结束，不宣称所有品牌/ROM 可用。

## M4 合并后的复验（2026-10-16）

以已合入 M4 的 `main` / `1b5c355` 为整合基线。合并保留 M4 的可靠性/分发保护与 M5 的 Android 原生模块、运行时和递归 WinUI 资源发布；版本预检同时接受 schema 1 和 2，继续拒绝未来版本，避免 M4 的新版本保护误拒绝旧桌面工程。

- `artifacts/m5-merge-build.log`：Release 0 警告、0 错误，7 组原生及 137 项默认托管测试通过（App 7、Capture 6、Domain 79、Workspace 45）。Hardware 类别仍未运行。
- `artifacts/validation/m5/merge-20261016-retry/`：完整 Android 媒体/断流/进程终止恢复复验通过；独立核查 17 个 MP4、两幅实时原生预览、手机 PCM 混音与 AAC 导出。首次复验在 ADB `start-server` 返回非零后中止；随后单独启动固定 ADB 成功，再用新目录完整重跑通过。首次与重跑日志分别为 `artifacts/m5-merge-validation.log`、`artifacts/m5-merge-validation-retry.log`。
- `artifacts/packages/m5-merge-20261016/`：自包含未签名 MSIX / ZIP 候选生成成功；`verify-package.cmd -SmokeTest` 校验 540 个载荷文件、归档内容、分发校验和及本机启动。证据为 `artifacts/m5-merge-package.log`、`artifacts/m5-merge-package-verify.log`。
- `artifacts/validation/m5/merge-package-ui-20261016/android-ui-validation.json`：候选包在仅含 Windows 系统路径、移除外部 .NET 配置后完成 Android 页面检查，`success: true`；包内 Android DLL 的 SHA-256 与本次验证的原生构建一致。

这些新增证据仍是本机自动验证，不补齐上述真机、长时同步、签名或干净机器安装验收。
