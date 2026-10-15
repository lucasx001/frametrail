# 摄像头连续分段、帧率与胶囊拖拽验证

日期：2026-10-15。范围：0003、0005、0006 的定向修复，不关闭完整硬件及长时验收清单。

## 实现

- 摄像头源按设备共享生命周期。重叠分段复用一个 Source Reader / Media Source，只创建独立文件编码器；最后一个文件结束后关闭设备。格式不同的并发请求明确失败，不改动正在使用的设备格式。
- 每个文件有独立四帧队列、停止边界和 IMFSample 元数据；共享只读负载缓冲区。最近的边界前画面作为下一文件的零时刻画面，避免等待下一摄像头 tick 时出现空白。不以复制帧增加持续采集帧率。初始化期间丢弃的旧预滚不计为录制丢帧。
- 含摄像头的自动 checkpoint 与普通路径一样先启动下一段，再以新屏幕首帧 QPC 截断旧段。启动失败保留当前录制；只有 finalize 并 probe 的媒体进入工程 / 恢复清单。暂停仍释放设备，恢复建立新源。
- 屏幕硬件编码器支持 D3D11 时，使用 DXGI Device Manager 与 MF Video Sample Allocator 直接提交 GPU NV12 样本。池最多 16 个样本，由编码器释放后回收，避免异步编码时覆盖纹理。初始化不支持时保留有界 CPU 回读路径，软件分辨率 / 帧率限制不变。关闭 MF 运行时前释放分配器。
- 禁用录屏 writer 的重复节流；采集与预览改用按绝对时间就近划分的帧槽，容忍 vsync 微小抖动；忙碌时跳过槽，不排队补帧。
- 添加独立性能查询，保留原 capture-status ABI。每段保存原始接收 / 写入 / 丢帧计数、转换 / 写入耗时、GPU 样本模式及摄像头源身份，便于后续定位。
- 胶囊空白区域、时长区域及右侧拖动点支持鼠标拖拽；输入控件及按钮排除。按下事件确定起点、桌面物理坐标计算移动、捕获丢失 / 松开时释放状态，避免窗口移动反馈与批量输入的旧位置问题。

## 本机与自动测试

- Windows 11 x64 build 26300；NVIDIA RTX 4060 Laptop GPU（32.0.16.1692）与 AMD Radeon 610M（32.0.21025.1024）；USB2.0 HD UVC WebCam，1280×720 / 30 FPS。
- Release 原生及 WinUI 构建成功，0 警告、0 错误。5 组原生测试、106 项默认托管测试通过（App 6、Capture 6、Domain 68、Workspace 26）。硬件类音频测试不在默认集合中。
- 结束 QA 后正常关闭停在准备状态的旧实例，更新常规 `src/FrameTrail.App/bin/x64/Release/net8.0-windows10.0.26100.0/win-x64` 目录并重启；Capture / Recording DLL 哈希与最终原生构建一致。最终启动不使用 QA 的 `--inspect-ui` 参数，保留正常窗口排除行为。
- 新增摄像头重叠文件时间戳隔离、负载共享、慢编码器队列上限与设备失败传播测试；协调器覆盖摄像头 checkpoint 启动顺序和失败后继续录制；C++ / C# 覆盖 60 Hz 抖动，托管测试同时覆盖高刷新率限速及慢消费者跳槽。

## 摄像头及产品编排

`artifacts/validation/capture-continuity/camera/camera-continuity.json`：四个连续文件、三次切换始终使用源身份 1；最后关闭后重开为身份 2。各文件首偏移均为 0，独立 ffmpeg 完整解码通过。边界前最后一帧距离切换点分别约 8.69 / 8.33 / 2.32 ms；下一文件零时刻延续最近画面。该量反映取样间隔，不是设备重新打开的等待时间。

`multitrack.frametrailproject.result.json`：真实产品协调器录制自有 1080p 动画窗口及摄像头，5 秒 checkpoint，7 秒后暂停、恢复后再录 3 秒。得到 3 段、6 个媒体引用、2 个标记、10.1049079 秒源时间；保存、原生校验和重开通过，warnings 为空。每段日志中的摄像头源身份为 1 / 1 / 2（暂停后重开），首偏移和录制丢帧均为 0，摄像头约 30 FPS。全部六个 MP4 独立解码通过。该用例不采集桌面、音频或光标。

## 帧率和转换耗时

基准程序只创建并录制自己的 D3D11 动画窗口，不录桌面、麦克风或摄像头。GPU 画面变化快于采集上限，计数不靠重复帧填充。

| 路径 / 记录 | 时长 | 写入帧 | 平均 FPS | 转换平均 / 峰值 | 队列丢帧 |
|---|---:|---:|---:|---:|---:|
| 修改前 CPU 回读 `cpu-profile.json` | 12.000 s | 712 | 59.33 | 2.316 / 11.533 ms | 0 |
| GPU 样本 `gpu-30s.json` | 30.002 s | 1777 | 59.23 | 0.153 / 1.191 ms | 0 |
| GPU 样本最终复测 `gpu-final.json` | 12.009 s | 711 | 59.21 | 0.201 / 1.408 ms | 0 |

30 秒 GPU 文件独立解码得到 1777 个不同帧哈希（`gpu-decoded.json`），与容器帧数和 writer 计数一致，队列高水位 1，确认硬件编码。代表帧 `gpu-frame.png` 检查通过。

此次受控基准在旧路径上也接近 60 FPS，没有复现先前后台构建期间 44.14 FPS 的 10 分钟结果。因此只能确认 GPU 转换 / 提交耗时明显降低，不能把两个不同负载的结果描述为同条件从 44 提升到 60，或声称长时目标通过。

WinUI 实际预览：`editor1080/playback-performance.json`，1920×1080 画布与源视频，2.0148 秒完成 122 次合成展示，约 60.55 FPS（短窗口计数存在一个帧槽的边界量化）。计时排除播放音频初始化 / 停止开销，保留 dispatcher 调度和实际 render 完成耗时。编辑器完整回归 `editor1080/editor-validation.json` 成功，包括播放中主题切换、X/Y 输入、slider/switch、保存、剪辑与撤销。

## 实际鼠标拖拽

Computer Use 操作新版 WinUI 胶囊：右侧拖点将桌面位置从 (1055,657) 移至 (911,633)；胶囊空白处继续移至 (791,664)。松开后点击来源按钮由显示器切为窗口，位置保持 (791,664)。`drag-final.txt` 保留按下、移动与捕获释放的显式 QA 记录。只验证本机当前 DPI 的鼠标操作，不代表触摸或跨屏 DPI 矩阵。

## 复现入口及边界

- `FrameTrail.Capture.Benchmark.exe 30 <新输出.mp4>`：受控 1080p60 上限录屏与阶段计时；`--surface 25` 只显示测试动画，供联合录制选择。
- `FrameTrail.Workspace.Smoke --camera-continuity <新目录>`：明确启用摄像头，连续文件切换与最终重开。
- `FrameTrail.Workspace.Smoke <新工程> --fixture --camera --checkpoint --fps60`：选择已运行的测试动画窗口，关闭音频和光标，执行产品分段 / 暂停 / 恢复。
- `FrameTrail.App.exe --editor <一次性工程副本> --m2-self-test <结果目录>`：会修改工程副本并退出；录制条 `--inspect-ui --drag-diagnostics <日志路径>` 用于鼠标事件 QA。

30 分钟多轨音画同步、29.97 FPS / 其他摄像头、拔插、GPU device lost、多适配器、稳定版 Windows、跨屏 DPI、4K60 和新 GPU 路径下的长时资源曲线仍待验证。导出编码仍沿用 CPU 回读；本次优化只改录制，不据此宣称导出零拷贝。

技术依据：[MF writer 节流](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sink-writer-disable-throttling)、[Sink Writer D3D Manager](https://learn.microsoft.com/en-us/windows/win32/medfound/mf-sink-writer-d3d-manager)、[DXGI 视频样本分配器](https://learn.microsoft.com/en-us/windows/win32/api/mfapi/nf-mfapi-mfcreatevideosampleallocatorex)、[样本池上限](https://learn.microsoft.com/en-us/windows/win32/api/mfidl/nf-mfidl-imfvideosampleallocatorex-initializesampleallocatorex)。
