# R2：录制检查点与增量自动运镜验证

日期：2026-10-25。对应 [Open Screen 审阅 R2](../reviews/2026-10-25-openscreen-review.md)。本记录覆盖独立 R2 工作区的实现与验证；主分支集成、整体 UI 和真实长录制由主 agent 复核。

## 实现

- 每个完成分段先保存素材快照和 recovery manifest，再排入可取消的串行派生规划任务。JSONL 读取与 native 运镜求值在录制转换锁外执行；提交只合并派生效果到最新快照，保留新素材、人工效果与更新的修订号。
- 暂停、停止、主来源关闭均在锁外等待对应规划屏障，完成状态仍保证可直接打开已有运镜。Dispose 可以取消尚未完成的派生工作，素材检查点先前已落盘。
- `AutomaticZoomPlanner` 在同一次录制中只读取新增 pointer 文件。每批最多 8192 个有效活动事件；不保留历史原始事件列表，也没有录制全生命周期 100000 条事件的规划上限。
- 原生 `autoZoomAppend` 使用 `frametrail-incremental-v1` 版本标记。最后的自动效果保留尚未闭合活动段的摘要，旧效果不重复生成。活动摘要最多保留 128 个近邻时间，单个 focus track 最多 2048 个 key；更长轨迹拆成连续效果。
- 拆分效果使用独立 `focusTransition`、首 key 的 `fromX` / `fromY` 延续焦点；裁切时使用 `phaseTicks` 保留已有动画相位。复制、移动均只调整实际 key 的绝对源时间，相对相位保持，粘贴到源时间零也不会生成负 key 时间。
- 同一 `ProjectWorkspace` 会话缓存已验证完成媒体。缓存签名包含完整 MediaAsset、路径、文件 ID / 卷号、大小、创建与修改时间；每次仍解析路径、打开文件并检查签名。删除、替换、元数据改变或重新打开工程会重新验证。验证失败的候选不填充缓存。

## 兼容性与语义边界

- 原 `autoZoom` / DogSC 参考路径保留，显式编辑器 API 的行为与测试继续存在；该旧 API 不用于新录制的检查点规划。
- 点击启动、持续活动保持、静止后退出；人工修改改为 `origin=edited` 并移除私有 `plannerState`，不会被后续规划覆盖。原有暂停前后点击可合并成连续运镜的契约保留。
- 正常输入的参考样本与旧规划器逐 10ms 求值一致。密集跨位置点击采用明确的新规则：焦点 key 不晚于相应点击，避免原 160ms 最小间距把 key 推到未来并使结果依赖检查点切分。它属于版本化规划器的语义变化，旧工程不自动重算。
- 实际 focus key 的 ticks 仍递增且位于效果区间内；`focusTransition` 为 0–5 秒，`fromX` / `fromY` 为 0–1，`phaseTicks` 是 0–50000000 的整数相对相位。
- 工程整体仍受现有 16 MiB JSON、效果数量和媒体数量限制。每次保存仍检查文件签名，原生项目验证仍随保存的工程规模增长；本次不宣称长工程所有操作均为 O(1)。

## 自动测试

独立工作区：`C:/Users/lucas/.codex/worktrees/r2-incremental-zoom/frametrail`。
共享只读工具链：`C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain`；原生构建产物完全在 R2 工作区生成，没有复用主工作区的增量构建目录。

```powershell
.\scripts\build.cmd -Configuration Release `
  -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain `
  -NativeBuildDirectory "$PWD/artifacts/native-release" -NativeOnly

.\scripts\test.cmd -Configuration Release `
  -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain `
  -NativeBuildDirectory "$PWD/artifacts/native-release" -LockedRestore
```

覆盖项：

- 单次规划与 20 种随机批大小输出的效果参数完全一致；恰好 1.4 秒点击间隔、进入下一点击前的 126ms 边界也覆盖每个可能切分点。
- 120000 次连续点击重定向，超过旧总事件上限；每条 focus track 不超过 2048 key，拆分处没有缩放脉冲或焦点跳跃。
- 拆分后的 start/end 裁边、移动逐毫秒焦点轨迹保持；复制保留焦点起点与相位，属性粘贴保留目标的轨迹和 focusTransition，人工编辑移除派生状态。
- 停止时规划被屏障阻塞，磁盘已经有可恢复工程和 manifest；转换锁仍可获取。主来源关闭在规划完成前不报告 RecordingComplete。
- 较早结果完成时不回滚较新的已完成素材；更高人工修订和重叠人工运镜优先。
- Dispose 取消阻塞的派生任务，等待已运行工作结束后释放工程，重开仍有完成素材。
- 已消费 pointer 文件删除后，增量规划仍不重新读取它；缓存媒体替换即使保持大小与时间，也因文件 ID 改变重新验证；失败候选和重新打开不会盲目信任缓存。

结果：原生构建成功，8/8 原生测试组通过；托管测试 App 49、Capture 6、Domain 111、Workspace 87，通过合计 253。Audio 托管测试受 `Category!=Hardware` 过滤而未执行。
证据：`artifacts/native-release/Testing/Temporary/LastTest.log` 和 `artifacts/test-results/*.trx`。

## 合成长输入基准

测试构造 10Hz 连续活动 JSONL，一次点击启动运镜，再连续移动；不进行真实设备录制。以下为同机 Release 结果，耗时只衡量解析与规划，不包含文件生成，不能作为 3 小时实机录制验收。

| 虚拟时长 | 活动事件 | 原文件读取 | 最大 batch | 规划耗时 | 最终效果参数大小 |
|---|---:|---:|---:|---:|---:|
| 30 分钟 | 17999 | 1 次 | 8192 | 161ms | 737 字节 |
| 60 分钟 | 35999 | 1 次 | 8192 | 343ms | 737 字节 |
| 180 分钟 | 107999 | 1 次 | 8192 | 1231ms | 741 字节 |

GC 累计分配随输入增长，并非峰值内存；这里只验证原始事件批和保存摘要有界，没有宣称总分配恒定。真实联合音画 30/60/180 分钟、不同磁盘和硬件、录制时停止延迟分布仍需单独验收。
