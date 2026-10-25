# R3：离线导出流式混音编码

日期：2026-10-25。范围：Open Screen 对照审阅中的 R3。

## 实现

- MP4/M4A 导出在 `BeginWriting` 后直接消费 `Session::mix_pcm` 的有界 PCM 块，不再先生成完整 `.frametrail-*.wav`。原始源音轨和显式 `PrepareAudio` 的 RIFF/RF64 接口仍可使用。
- 每个编辑片段只执行一次连续 WSOLA 混音，保留搜索和重叠相加状态；每个 AAC 输入包最多 1024 个双声道采样帧，视频按对应输出时间交错调度。没有为每个包重新调用 mixer，没有新增整片音频缓存或生产者线程。
- 混音与播放、峰值和兼容 PCM 准备共用实现。导出抑制 mixer 的状态/进度覆盖：视频任务继续按视频帧报告；M4A 按采样帧报告。尾部核对累计混音帧数与时间线采样帧数，保留不足整帧的视频尾部。
- 继续使用任务临时 MP4、最终解码验证、原子替换；错误和取消不替换原目标。GPU 拒绝后的整次 CPU 重试重新创建局部 mixer 状态，未把它移入持久会话缓存。

## 验证命令

在独立 worktree 中使用共享只读工具链，构建产物写入该 worktree：

```powershell
.\scripts\build.cmd -Configuration Release -NativeOnly -NativeBuildDirectory "$PWD\artifacts\native-release" -ToolchainRoot C:\Users\lucas\Desktop\project\frametrail\.tools\toolchain
.\scripts\validate-m2.cmd -ToolchainRoot C:\Users\lucas\Desktop\project\frametrail\.tools\toolchain -OutputDirectory artifacts\validation\r3-streaming-final
```

初始化 CMake 使用主工作区已有的 nlohmann_json 源码缓存，不修改该缓存。

## 结果

- 原生干净基线构建通过，8/8 原生测试组通过；改动后的原生增量构建与 8/8 原生测试组通过。
- 扩展 M2 smoke 和独立 ffmpeg/ffprobe 校验通过。证据位于执行 worktree 的 `artifacts/validation/r3-streaming-final/m2-validation.json` 和 `image-audio-metrics.json`。
- 剪切、重排、全局/片段静音与增益、2x 变速：视频 2.25 秒，音频 2.261312 秒（误差小于一个 AAC 包）。30 FPS 输出 68 帧、60 FPS 输出 135 帧，均包含尾部。
- 0.25/0.5/1/1.5/2/4x M4A 与相同连续 mixer 的 WAV 参考在相同采样位置比较：相关系数均大于 0.999994，SNR 49.87–50.91 dB；参考音调均为 440 Hz。AAC 解码尾部填充不超过 1024 帧。
- 三个不同 rate（1.5x、0.25x、4x）的非整秒、非整视频帧 source 区间连续导出：输出 47,263,373 ticks（4.7263373 秒），PCM 226,864 帧，视频 142 帧。M4A、默认 MP4、强制 CPU MP4 的音频相关系数均为 0.99999079，SNR 47.30 dB。
- CPU MP4 统计为 142 个 CPU 帧、0 个 GPU 帧。默认路径能够完成；没有注入编码器拒绝来强制触发自动重试。
- 30 分钟逻辑音频工程在 45 毫秒内已开始编码，随后中途取消为 stage 5；原目标内容保留，任务临时文件清理。
- 文件系统创建监听确认所有 MP4/M4A 导出和取消期间没有创建 `.frametrail-*.wav`。占用目标与视频任务取消回归也通过。

## 验收边界

短合成样本证明样本对齐、多个 rate、AAC 尾部、取消和临时文件行为；30 分钟逻辑工程只验证开始编码和中途取消，没有完整导出 30 分钟。没有执行长时语音/音乐听测、真实多小时完整导出、磁盘不足注入或跨编码驱动矩阵。原生峰值缓存和显式 PCM 导出仍然可能生成用户要求的 WAV，它们不属于本次去除的任务临时音轨。
