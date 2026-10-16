# FrameTrail 应用图标接入验证

日期：2026-10-16。范围：将已选定的 v1 图标接入 Windows 应用及候选分发包。

## 实现

- `FrameTrail.App.csproj` 的 `ApplicationIcon` 指向已批准的多尺寸 ICO，生成 EXE 内嵌图标；ICO 同时复制到构建和发布目录。
- `AppIcon.Apply` 使用 `AppContext.BaseDirectory` 定位 ICO，通过 `AppWindow.SetIcon` 设置窗口图标，不依赖启动时的工作目录。
- 共享浮动窗口配置覆盖录屏胶囊、来源操作条、录制完成卡和备忘录；编辑器、设置和 Android 连接窗口分别调用相同入口。
- 57 个透明 PNG 由同版原图生成：44/50/150 px 基准，125%/150%/200%/400% 缩放，以及 14 种目标尺寸的默认、unplated、lightunplated 资源。脚本只缩放和转换格式，不改图案。
- 安装包继续使用原有清单中的 `PackageAssets` 路径。构建将 PNG 索引进 `FrameTrail.App.pri`；打包再通过 PRI indexer 合入包身份 `FrameTrail.Desktop` 的 `resources.pri`，同时保留嵌入 XAML，保证便携包的正常启动。

## 已验证

- Release 构建：0 警告、0 错误。运行中的旧胶囊处于录制准备状态，正常关闭后更新常规 Release 输出。
- EXE 图标：通过 Windows `ExtractAssociatedIcon` 从实际生成的 EXE 提取，视觉核对为新图案，证据在 `artifacts/brand-validation/exe-icon.png`。
- 全部 57 个 PNG：逐一校验预期宽高和透明角落；发布 ICO 与源 ICO 的 SHA-256 一致。
- 最终包 PRI：主资源图为 `FrameTrail.Desktop`，三个 `Files/PackageAssets/*.png` 资源包含全部 57 个候选，且保留 `App.xbf` 嵌入数据。避免只索引图标而丢失 WinUI 资源。
- `artifacts/packages/icon-v1-final`：自包含 ZIP 和未签名 MSIX 生成成功；`verify-package.cmd -SmokeTest` 校验 596 个载荷文件、两个压缩包的逐文件内容和分发校验和通过。
- 候选包启动：`windowLoaded=true`、`success=true`，100 次引擎生命周期检查通过，使用候选目录内的本地运行时和原生模块。
- 普通 Release 应用已重新启动，录制准备窗口可通过 Windows UI Automation 读取。

## 验证命令与证据

```powershell
& "$env:LOCALAPPDATA\FrameTrail\Toolchain\dotnet\dotnet.exe" build src\FrameTrail.App\FrameTrail.App.csproj --no-restore -c Release -p:Platform=x64 "-p:NativeBuildDirectory=$PWD\artifacts\native-release"
.\scripts\package.cmd -OutputDirectory artifacts\packages\icon-v1-final -NativeBuildDirectory "$PWD\artifacts\native-release" -SkipBuild
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\icon-v1-final -SmokeTest
```

完整证据保存在本机 `artifacts/brand-validation/` 和最终候选目录。未执行安装、发布或 Windows 图标缓存重置；开始菜单安装态、旧固定快捷方式缓存及跨显示器 DPI 矩阵尚未验收。本轮只验证图标接入相关构建、资产和启动，不代替录制/剪辑/导出的媒体回归。

## 技术依据

- [Microsoft：AppWindow.SetIcon，ICO 与应用目录路径](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.seticon?view=windows-app-sdk-1.8)
- [Microsoft：Windows 应用图标尺寸与明暗无底板资源](https://learn.microsoft.com/en-us/windows/apps/design/iconography/app-icon-construction)
- [Microsoft：MakePRI 的 PRI indexer 与资源合并](https://learn.microsoft.com/en-us/windows/uwp/app-resources/makepri-exe-format-specific-indexers)
