# 0031 — Open Screen 对照审阅的五项实现修复

日期：2026-10-25。状态：本机实现已完成；集成证据与未执行范围见 [主 agent 验证](validation/0031-review-fixes.md)。来源：[审阅报告](reviews/2026-10-25-openscreen-review.md)。

用户本轮选择仅执行 R1–R5 五项实现问题。箭头/编号、摄像头让位、选区导出/GIF、字幕等功能建议保留为后续产品取舍，不纳入本任务。

| 任务 | 实现 | 责任与证据 |
|---|---|---|
| R1 光标连续性 | 偏移后选择 pointer 文件，连续检查点共享有界历史；保留逻辑剪辑边界与文件形状来源 | 独立 sub-agent；[R1](validation/r1-cursor-continuity.md) |
| R2 增量运镜 | 素材检查点先落盘；新增 pointer 分批规划，锁外计算、校验修订后合并；会话内完成媒体验证缓存 | 独立 sub-agent；[R2](validation/r2-incremental-zoom.md) |
| R3 流式导出 | 连续 mixer 直接交错写 AAC/视频，取消整片任务临时 WAV；保留 WSOLA、取消和原子提交 | 独立 sub-agent；[R3](validation/r3-streaming-export.md) |
| R4 编辑器职责 | 提取播放、手势、素材任务、导出服务，测试取消与释放顺序；修复进度回调异常时的释放风险 | 独立 sub-agent；[R4](validation/r4-editor-controllers.md) |
| R5 测试与规格 | 新增统一三链窗口自测，修正文档并区分 CPU、应用方法和真实输入证据 | 独立 sub-agent；[R5](validation/r5-test-spec-alignment.md)、[测试入口](testing.md) |

主 agent 集成后逐项审查源时间边界、规划/保存修订、原生任务取消、UI 控制器生命周期及测试声明。另发现并修复缩略图任务被取消后遗留 inFlight 标记的问题；新增回归在修复前失败、修复后通过。

## 验收

- [x] 五个独立任务完成，本地提交已统一集成并 review。
- [x] 光标正负偏移、静止隐藏、跨 checkpoint 点击反馈的真实 PNG/MP4 像素回归。
- [x] 增量规划随机分批、高活动长轨、修订合并、停止/关闭屏障和文件替换缓存失效测试。
- [x] MP4/M4A 多速率、非整秒、取消、原目标保护与独立解码。
- [x] 控制器替换/取消/所有权、导出生命周期测试与完整 Release 构建。
- [x] 三条统一窗口应用方法工作流及导出独立解码。
- [ ] 跨 GPU/DPI/设备、真实长时联合音画、完整多小时导出和发行矩阵；沿用 0010–0013 的独立验收。

实际鼠标/键盘的单独走查与证据层级记录于主 agent 验证页；应用自测结果本身保持 `actualInputVerified=false`。
