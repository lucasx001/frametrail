# FrameTrail icon · v1

深灰圆角底上，以银白画框、蓝色叠帧和向右的播放符号，表达屏幕捕捉、连续画面与剪辑。配色取自应用现有主题：`#101113`、`#191A1C`、`#F5F5F5`、`#5AAEFF`。

- `FrameTrail.Icon.v1.png`：内置 ImageGen 生成的原始透明 PNG，保留原始画面和透明通道。
- `FrameTrail.Icon.v1.ico`：同一画面的 Windows 图标，包含 16、20、24、32、40、48、64、128、256 px，32 位透明通道；仅做尺寸缩放和格式封装。
- ICO 已用于 EXE 内嵌图标及录屏胶囊、编辑器、设置、Android 连接、备忘录、来源操作条和录制完成窗口；运行时使用应用目录下的绝对路径，不依赖工作目录。
- `src/FrameTrail.App/PackageAssets/`：同一 PNG 生成的 57 个安装包图标，含 44/50/150 px 基准、125%–400% 缩放及 16–256 px 的默认/明暗无底板资源。随 build/publish 复制到 `PackageAssets/` 并进入 PRI，供已有 MSIX 清单使用。
- 重新生成安装包 PNG：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/generate-icon-assets.ps1`。只缩放已批准原图，不修改设计。

## Generation

Mode: built-in `image_gen` tool. Transparent background enabled.

### Prompt

```text
Use case: logo-brand.
Asset type: finished Windows desktop application icon for FrameTrail, a screen recording and elegant demo-video editing app.
Primary request: design ONE refined, minimal, memorable app icon that expresses captured frames becoming a flowing video timeline.
Composition: a single large rounded-square charcoal icon tile, face-on, perfectly centered on a square transparent canvas, with only 5 percent transparent safety margin around the tile. The tile fills 90 percent of the image. Inside it, one bold geometric symbol fills about 60 percent of the tile. The symbol consists of two subtly offset rounded screen-frame shapes: a rear frame in clear sky blue peeks out at the lower-left, and a dominant front frame in soft luminous silver-white. The front frame has an intentional opening on its right side, where a simple small sky-blue right-pointing play shape continues outward as a short clean motion trail. Design these as a cohesive proprietary mark with a unified geometry, generous negative space and softly rounded ends. Limit the interior to this frame and motion motif; avoid extraneous strokes. Aim for a beautiful balanced silhouette, readable at 32 pixels.
Color palette grounded in the existing app: charcoal #101113 / #191A1C tile, silver-white #F5F5F5 primary symbol, sky blue #5AAEFF accent. Use very restrained smooth tonal shading only to give the tile a tactile satin surface. The symbol is crisp and nearly flat, without glass effects.
Style: restrained premium creative software identity, original geometric vector-like design, precise rounded geometry, calm, elegant, exceptionally clean edges. Flat front view, no perspective. This is the actual isolated app icon asset, not a presentation board or mockup.
Text: none. No letters, no wordmark, no labels, no numbers, no watermark.
Avoid: camera lenses, scissors, film sprockets, cursor arrows, recording red dots, microphones, sparkles, gradients in many hues, neon, glow, glossy 3D, excessive shadows, textures, desktop UI, device mockups, comparison grids, any extra objects outside the tile.
Transparent background outside the rounded-square icon tile.
```
