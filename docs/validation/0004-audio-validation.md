# 0004 音频采集与共同 QPC 时钟验证

> 历史执行快照。后续集成/修复见 [capture-continuity-performance.md](capture-continuity-performance.md)；当前任务状态以 [progress.md](../progress.md) 和 [待办](../todo.md) 为准。本页未勾选项仅反映记录当日范围。


日期：2026-10-14。此记录区分已实现代码、已运行检查和仍需实机验收项目；任务与总进度由主 agent 统一更新。

## 实现与集成约定

- `native/FrameTrail.Engine/audio` 构建 `FrameTrail.Audio.dll`。系统音使用选定输出 endpoint 的 WASAPI shared-mode loopback；麦克风使用选定 capture endpoint；应用音使用 `ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`，包含目标 PID 的进程树。
- Application Loopback 运行时检测系统 build >= 20348；`ft_audio_process_from_window` 将有效 HWND 映射至 PID。窗口粒度不等于浏览器标签页或独立窗口音频隔离。
- 每个 handle 一条独立源轨；新建 WAV，IEEE float 32-bit、48,000 Hz、stereo。初始化显式请求 `AUTOCONVERTPCM | SRC_DEFAULT_QUALITY`；设备原 mix rate/channels 保留在状态中，应用进程虚拟设备没有对应物理 mix format，原生格式字段为 0。
- 应用先启动音频，视频首帧后调用 `SetEpoch(firstFrameQpc, qpcFrequency)`。最多缓存 2 秒前导音频；首帧前样本裁除，较晚启动的有效源以静音补齐至共同零点。
- 暂停、恢复、停止必须由编排层各取一次 raw QPC，向屏幕和所有音频 handle 传同一个边界。跨暂停缓冲按样本裁切，暂停左边界排除、恢复边界包含；已写过暂停边界的部分通过文件截断回收。
- WASAPI `GetBuffer` 的 QPC 是 100 ns 单位，代码明确转换为 raw QPC，不能直接与原始计数混用。设备 sample position、原始 QPC100ns、转换后的 raw QPC、帧数及 discontinuity/timestamp-error flags 写入可选 JSONL，不记录音频样本。
- 相邻有效包时间在 nominal ±1% 内时，用有界线性插值修正微小设备时钟漂移；更大的跳跃以显式 gap/overlap 统计处理。Windows SRC 负责设备格式到 48 kHz 的采样率转换。此校正路径仍需长时质量验收。
- 电平按 20 Hz 更新状态。设备失效返回 HRESULT 和明确 Failed 状态并保留已写数据；默认设备变更只提示，保持初始设备，避免静默切换来源。
- 文件不覆盖既有输出；每秒刷新 WAV header/checkpoint，正常停止最终更新 header、flush 并关闭句柄。RIFF 4 GB 上限显式返回 `ERROR_FILE_TOO_LARGE`，需要编排层在上限前分段，不能把单条无限长 WAV 作为已实现能力。

根 CMake 需要添加 `add_subdirectory(native/FrameTrail.Engine/audio)`；`BUILD_TESTING` 下添加 `add_subdirectory(tests/FrameTrail.Audio.Tests)`。现有 App 的 `FrameTrail.*.dll` 复制规则可覆盖本 DLL。

托管入口：`AudioCaptureSession.Start(AudioCaptureOptions)`、`SetEpoch`、`PauseAt`、`ResumeAt`、`StopAt`、`GetStatus`。Start/Stop/Dispose 包含设备和文件等待，必须在 UI Dispatcher 外运行；每个会话通过 SafeHandle 和锁保护生命周期。失败后允许读取状态，随后销毁，不能在失败 handle 上继续录制。

## 本次实际环境

- Windows 11 家庭版中文版，10.0.26300，x64。
- AMD Ryzen 9 7940HX，物理内存 16,340,406,272 bytes。
- 实际音频设备：Realtek High Definition Audio、NVIDIA High Definition Audio、NVIDIA Virtual Audio Device。
- 本次默认输出和麦克风协商格式均为 48 kHz / 2 channels；没有使用 44.1 kHz 或 mono 外设。
- MSVC 14.44.35207，Windows SDK 10.0.26100.0，.NET SDK 8.0.426。

## 已运行与结果

1. MSVC `/std:c++20 /W4 /EHsc /MD` 构建 DLL 和 native tests 成功，0 警告。
2. 原生时钟检查通过：首帧前裁除、精确暂停左/右边界、多次暂停、错误调用顺序、30 分钟整数时钟映射不累积误差，以及 null/invalid ABI 参数。这里的 30 分钟是数学映射测试，不是 30 分钟实录。
3. .NET `FrameTrail.Interop` 构建成功，0 警告。
4. `.NET` 音频集成测试 **4 / 4 通过**：能力与无效 HWND、设备结构体枚举、静音应用音轨的精确暂停时长与 SafeHandle 生命周期、拒绝覆盖既有输出。
5. 运行真实设备 smoke：测试进程通过 waveOut 播放一秒低幅度 1 kHz 正弦；系统音和当前进程树音轨均捕获到该信号；麦克风轨也收到真实采样。各轨均暂停 200 ms，最终 WAV 样本数与相同 raw QPC 边界计算结果完全一致。
6. 静音 Application Loopback 录制约 350 ms 可正常结束并产生有时长的 PCM 文件；不等待非静音包才停止。

最新 smoke（`build/audio/smoke-20261014-225700`）的摘要：

| 来源 | 最终 frames | QPC 预期 frames | packets | 峰值 | 结果 |
|---|---:|---:|---:|---:|---|
| 默认输出 loopback | 56,053 | 56,053 | 164 | 0.100403 | 通过 |
| 当前进程树 | 53,380 | 53,380 | 146 | 0.0515647 | 通过 |
| 默认麦克风 | 48,926 | 48,926 | 150 | 0.0267508 | 通过 |
| 静音进程树 | 17,474 | 共同 QPC 停止后补齐 | 36 | 未作为信号质量指标 | 通过 |

不同轨道测试是依次运行，实际控制等待和 waveOut 初始化耗时不同，不能用总 frames 横向判断同步。原生 smoke 每次仍独立断言该轨的精确 QPC 预期帧数。

发现并修复：Stop 最初仅 flush、未关闭 WAV 文件句柄，托管读取出现 sharing violation；已改为停止 worker 后 finalize 并关闭音轨与诊断文件，重新运行全部检查通过。

Application Loopback 的实测原始 `devicePosition` 每包为 0；`DriftAvailable=false`，不会误报 -1,000,000 ppm。该来源仍按包的 QPC 定位。测试进程新建播放流时虚拟设备 QPC 有一次约 109 ms 跳跃，代码保留原始诊断并用静音补齐缺口；未将该情况当作长期同步通过。

证据：

- `build/audio/FrameTrail.Audio.Tests.exe`：native clock + `--smoke <new-directory>`。
- `build/audio/smoke-*`：独立 `.wav` 和按包 `.jsonl`；包含本机麦克风样本，仅用于本地验收，不上传。
- `build/audio/test-results/audio-tests.trx`：4 / 4 集成测试。
- 当前已验证 DLL SHA256：`A183BF7A26B61908EDA730A15D29035CF9918C166DEB7EE4BDDE17F20B7DB4C7`。

## 仍需验收

- [ ] 真实 30 分钟闪光/提示音视频素材，首尾音画误差 <= 50 ms；本轮没有与 WGC 成片做联合测量。
- [ ] 不同采样率（44.1 kHz/96 kHz）、mono、多声道外设的 Windows SRC 与声道映射质量。
- [ ] 受控微量重采样的长时音质与缺口/重叠阈值；短录时钟统计不能替代该项。
- [ ] 拔插麦克风、默认设备变更、禁用设备和音频服务中断的实机行为；未主动改变用户系统音频设置。
- [ ] 独立另一进程与多子进程信号的 include-tree 隔离试验。
- [ ] 多轮随机暂停/继续和屏幕/摄像头/光标共同编排；本轮只有数学重复暂停测试与单次真实暂停。
- [ ] RIFF 上限前项目自动分段、异常终止后 checkpoint 音频恢复和失败轨在项目清单中降级提示。
- [ ] WinUI 设备选择、音频电平、可解释的错误 UI 和模式范围文案由后续编排集成验收。

技术依据：[WASAPI GetBuffer 的时间单位](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)、[Application Loopback](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)、[Loopback Recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)。
