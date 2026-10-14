# FrameTrail project schema 1

状态：M0 领域模型已实现。日期：2026-10-14。

权威实现是 `native/FrameTrail.Engine/core`；C# 的 `FrameTrail.Domain` 仅负责快照 DTO 与传输格式校验。App 必须调用 `EngineSession.ValidateProject(snapshot)` 通过原生完整语义校验后，才可提交候选修订或保存。领域测试共用 `tests/FrameTrail.Core.Tests/fixtures/project-v1.json`。

## 顶层协议

字段使用严格区分大小写的 camelCase；UTF-8 JSON，最多 16 MiB，最多 32 层嵌套。重复键、未知字段、未知枚举值、缺失必需字段、整数溢出、非有限数值和未来 schema 均拒绝，防止读入后丢字段覆盖。`effects[].parameters` 是有意保留的扩展对象，允许任意有限 JSON 值并完整往返。

| 字段 | 类型 | 语义 |
|---|---|---|
| `schemaVersion` | 整数 | 必须为 1；不使用 DogSC schema 号 |
| `projectId` | 字符串 | 稳定工程身份 |
| `revision` | 非负 int64 | App 单调递增修订；Engine 同一 session 只接受同项目严格更新的修订 |
| `sourceDuration` | Time | 拼接后的原始录制全时长，正值，最多 7 天 |
| `media` | Media[] | 1–100000 项源轨道 manifest |
| `segments` | Segment[] | 1–100000 项成片顺序，数组顺序是输出顺序 |
| `canvas` | Canvas | 布局与 SDR 背景 |
| `effects` | Effect[] | 0–100000 项源时间效果；M0 只验证/保存包络 |
| `markers` | Marker[] | 可省略，默认空数组；最多 100000 项录制标记 |
| `export` | Export | 当前输出约束 |

所有 item ID（media、segment、effect、marker）在工程内全局唯一，只允许 ASCII 字母、数字、`-`、`_`，1–128 字节。项目 ID 同样遵循该字符约束。

## 时间与速率

```json
{ "ticks": 5000000, "timescale": 10000000 }
```

- `ticks` 为有符号 int64；`timescale` 是 1–1000000000 的整数。
- 外部时间先归一为每秒 10000000 ticks（100ns）；使用整数算术，最近舍入，正负半值均远离零。原始时间绝对值也不能超过 7 天。
- 源时间是暂停已扣除后的全局录制时间。媒体文件本地时间 = 源时间 − `startOffset`。
- 速率为 `{ "numerator": 1, "denominator": 4 }`。分子分母均为 1–1000000 的整数，播放速率限 0.25–4；无需预先约分。
- 成片片段长度 = `round(sourceDuration / rate)`，再以整数累加各片段长度；禁止浮点累计。量化为零时长的片段被拒绝。拆分可能产生最多一个 tick 的舍入差。
- 源时长最多 7 天，因此最慢速成片最多约 28 天；没有将整个成片时长错误地再限制到源时间上限。

所有片段与媒体覆盖区间为 `[start, end)`，精确切口归后继片段。默认最终端点不返回任何帧。C++ `EndpointPolicy::include_final_endpoint` 仅用于显式端点查询，返回**输出最后片段**的源端点；源查询若该时刻已属于另一个保留片段，正常半开区间所有权优先。预览和 C ABI 均坚持半开区间，不伪造最后一帧。

## 媒体 manifest

```json
{
  "id": "screen-1", "kind": "screen", "path": "media/screen-1.mp4",
  "duration": { "ticks": 10, "timescale": 1 },
  "startOffset": { "ticks": 0, "timescale": 1 },
  "width": 1920, "height": 1080,
  "sampleRate": 0, "channels": 0
}
```

`kind` 为 `screen`、`camera`、`systemAudio`、`microphone`、`pointerEvents`。宽高、采样率与声道数可省略，缺省 0；视频必须有 1–32768 的宽高，音频必须有 8000–384000 的采样率及 1–32 个声道。每种类型的多个文件不能在源时间上重叠。

主屏幕媒体分段必须从 0 连续覆盖至 `sourceDuration`，不得重叠、缺口或超出源结束。辅助轨允许缺口和负起点，但每项必须与源时长相交。缺失摄像头时段不生成虚构摄像头画面。

路径只能是工程内相对路径，以 `/` 分隔；拒绝绝对路径、UNC、反斜杠、空目录段、`.`/`..`、ADS、控制字符、Windows 保留设备名、尾点/尾空格和非法文件名字符。中文路径受支持。这是词法检查；持久化层还必须检查实际路径/reparse point，避免目录联接逃逸。当前 Core 不访问磁盘，也不以存在性检查代替安全解析。

## 编辑序列与光标

```json
{
  "id": "clip-1",
  "sourceStart": { "ticks": 4, "timescale": 1 },
  "sourceDuration": { "ticks": 2, "timescale": 1 },
  "rate": { "numerator": 1, "denominator": 1 }
}
```

源区间必须非空、在总源时长内且互不重叠。允许重排，拒绝重复使用任何源区间。删减的时间不映射；camera/audio/pointer/marker 均应使用唯一的 `TimelineMap`。

Core `map_pointer_events` 接受归一后的 `{ticks,x,y,kind,cursor_id}`，位置属于 normalized content（0–1）。事件稳定按时间排序；相同时间原有顺序保留。返回每个剪辑一个 `PointerGroup`，禁止展平后跨剪辑插值。没有起点事件时，使用该片段源起点之前最近位置作为 `move` seed，不把旧点击重复成新点击。`sample_pointer(group,time)` 仅在组的半开输出区间内插值。

光标采集 JSONL 与上述内存结构的转换由 Capture/Workspace 完成；JSONL 的原始整数时钟不能直接当成 canonical ticks 使用。

## 画布与 FrameScene

- `canvas.width/height`：1–16384 输出物理像素。
- `canvas.padding`：0（含）到 0.5（不含），单位是最短画布边长的比例；不能留出空屏幕区域。
- `canvas.background`：`{r,g,b,a}`，每项有限且为 0–1 的 SDR 数值。
- `canvas.screen`：`{x,y,width,height}` 归一化画布区域，必须完全位于画布内。
- `canvas.camera`：`{enabled,x,y,width,height,mirror}`，同样归一化。

基础 FrameSceneEvaluator 根据成片时间求源时间，再选中该时刻的 screen/camera 媒体分段，输出媒体 ID、本地 tick、按比例居中的输出像素矩形、镜像与背景。screen 在区域内应用 padding 后按媒体比例 fit；camera 也执行等比例 fit。结果携带 revision 以拒绝过期预览。

本任务没有实现 GPU、圆角、阴影、缩放效果动画或音频时间伸缩；`effects` 存在不代表这些效果已渲染。

## 效果、标记、导出

Effect 必需字段为 `id`、`kind`、`sourceStart`、`sourceDuration`、`parameters`。kind 允许 `zoom/mask/sticker/focus/tilt/intro`；效果时间在源范围内且正时长。包络稳定，参数由后续效果实现按类型定义；当前完整保留参数，不解释高级效果。

Marker 为 `{id,time,label}`。time 为 0–sourceDuration（含末端）的源时间；label 可为空，UTF-8 最多 512 字节。成片显示时使用 TimelineMap；被删除区间的标记不进入成片。

Export 必需字段为 `width/height/fps/videoCodec/audioCodec/container`。当前约束：宽高为 2–7680 的偶数；FPS 为 30/1 或 60/1；`h264/aac/mp4`。这里只是工程设置校验，不保证当前机器有对应编码能力。其他导出产品能力在 M2 实现，不将未实现的编码格式作为已支持项。

## 原生桥接与修订事务

- `ft_project_validate(utf8,length)`：无状态完整校验；C# `EngineSession.ValidateProject(snapshot)` 先做协议校验，再调用此入口。
- `ft_session_load_project`：构造、校验不可变 evaluator，再在 session 锁内原子替换；拒绝变更 project ID 或相同/更旧修订。取消 session 不能重新装载。
- `ft_session_output_duration/map_output/map_source/evaluate_scene`：读取一个共享不可变快照，返回固定 ABI 结构；逐帧不传 JSON 或像素数组。
- 期望 absence 返回 `FT_E_OUT_OF_RANGE=9`；未装载返回 10；无效工程 7；过期修订 8。
- DTO 中的 IReadOnlyList 应被视为不可变输入。工作区需要通过序列化深快照或自有不可变集合隔离外部可变数组，在同一修订上不得改变列表内容。

## 参考与验证边界

已静态阅读相邻 DogSC 的 `Sources/RecorderCore/TimelineMap.swift`，参照其数组顺序决定输出顺序、禁止源重用、半开区间、按剪辑保留光标组与位置 seed 行为。FrameTrail 加入整数时间、有理速率、明确量化和路径协议约束。

`mapping-vectors.json` 是人工计算结果：源 6–10s 以 4 倍速 → 输出 0–1s；源 0–2s 以 0.25 倍速 → 输出 1–9s；源 4–6s 正常速率 → 输出 9–11s。C++ 直接执行映射，C# 通过真实 C ABI 执行同一组数据。没有 Mac 环境，未运行 Swift 对照程序，此项保持未验收。
