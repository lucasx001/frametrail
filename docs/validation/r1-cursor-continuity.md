# R1 光标跨恢复分段连续性

日期：2026-10-25。

## 实现

- 共享原生 renderer 在应用时间偏移之后选择 pointer 文件，跨连续恢复文件使用源时间取样。
- 光标平滑、静止隐藏的活动时间、0.55 秒点击反馈共享最多 8.2 秒的历史窗口。文件首样本不再自动成为一次新活动。
- 每个样本保留所属 pointer 文件，录制形状 ID 在不同文件复用时仍从正确的文件目录解析 PNG 和热点。
- 采样限于当前逻辑剪辑片段；时间校准在两端保持边界位置，不跨暂停、分割、裁切或重排边界。裁切起点可从素材中的前一位置初始化，但清除点击及跨剪切的平滑历史。
- 仅保留当前窗口所需文件；最多 128 个历史文件、65,536 个合并样本和 2,000,000 个原始缓存样本。超限明确报错。物理文件超过工程引用 duration 的尾部样本不参与取样。
- 无工程格式变更。当前录制器将物理 checkpoint 合并到同一个 Segment，而暂停/继续建立新 Segment，因此已有工程可以使用保守的剪辑边界。旧工程经过“恢复原始顺序”后若每文件一个 Segment，这些已有逻辑边界仍保留，不猜测其原始 take 身份。

## 自动验证

独立 worktree：`C:/Users/lucas/.codex/worktrees/r1-cursor-continuity/frametrail`。

```powershell
.\scripts\build.cmd -Configuration Release -NativeOnly -NativeBuildDirectory "$PWD\artifacts\native-release" -ToolchainRoot C:/Users/lucas/Desktop/project/frametrail/.tools/toolchain
python scripts/verify-cursor-continuity.py --native-bin artifacts/native-release/bin --output artifacts/validation/cursor-continuity-03
```

基线原生构建、8 组测试通过；修复后原生构建、9 组测试通过。新增 `FrameTrail.Media.PointerSampling` 直接测试 renderer 使用的采样实现，包括正负偏移、平滑、静止隐藏、点击持续、复用形状 ID、物理重叠尾部、暂停/剪切/重排、缺失 pointer 覆盖及历史上界。

媒体脚本生成两段 60 秒逻辑素材和 JSONL，直接调用构建的 `FrameTrail.Media.dll` 进行真实 Direct2D 预览 PNG 和 2 秒 H.264 MP4 导出，再用 ffmpeg 独立解码计数：

| 检查 | 结果 |
|---|---:|
| 59.9 秒 +0.2 秒偏移，预览位于第二文件的光标 | 60 个白像素 |
| 60.1 秒 -0.2 秒偏移，预览位于第一文件的光标 | 60 个白像素 |
| 正偏移导出第 27 帧的光标 | 60 个白像素 |
| 静止超过 8 秒跨 checkpoint 后的光标 | 0 个白像素 |
| checkpoint 前点击在下一文件仍显示的波纹 | 248 个蓝色像素 |

证据：`artifacts/validation/cursor-continuity-03/validation.json`、`project/*.png` 和 `positive.mp4`。脚本无需额外 Python 库，不录制桌面、不启动应用窗口、不触碰用户工程。

同一脚本指向修复前原仓库 `artifacts/native-release/bin` 的 DLL 时，正/负偏移预览和正偏移导出的白像素计数均为 0，断言按预期失败；验证能够捕获原缺陷。

## 尚未覆盖

- 实际鼠标操作、跨 DPI 光标尺寸/形状、不同 GPU 与多屏采集矩阵。
- 本次像素样片为 160×90 短导出，不能替代长时播放和高分辨率性能验收。
- 超长且异常密集的手写 pointer 文件会触发资源上限；不承诺任意密度的外部工程。
