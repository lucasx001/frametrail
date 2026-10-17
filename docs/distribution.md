# GitHub Releases、安装与更新

已提供 GitHub Release 工作流与每用户 Setup.exe，详见 [0019](0019-github-releases.md) 和 [验证记录](validation/github-releases.md)。正式签名、干净机器、上一版升级及 [0010](0010-reliability-performance.md) 的扩展验收仍需分别完成。

## 从 Releases 下载安装

打开 [FrameTrail Releases](https://github.com/lucasx001/frametrail/releases)，选择所需版本，下载 `FrameTrail-X.Y.Z-x64-Setup.exe`，双击后按步骤安装。安装在当前用户下，默认目录为 `%LOCALAPPDATA%\Programs\FrameTrail`，不需要管理员权限；可在 Windows「已安装的应用」中卸载。安装包包含 .NET、Windows App SDK、原生 DLL、Android 运行组件、素材和许可，无需开发工具链。

也可下载 `FrameTrail-X.Y.Z-x64-portable.zip`，完整解压后运行 `FrameTrail.App.exe`。仅支持 Windows 11 x64，API 最低 build 22621，不支持 Windows 10 或 ARM64 原生包。

当前 Setup.exe 没有商业代码签名，Windows 可能显示未知发布者或 SmartScreen 提示；校验值见同版本的 `SHA256SUMS.txt`。不要将文件校验等同于受信任的发布者签名。未签名 MSIX 不附加到公开 Release。尚未创建公开版本时，Releases 页面暂时没有可下载的安装包。

## 发布一个版本

工作流文件为 `.github/workflows/release.yml`，名称为 **Release Windows application**。工作流合并到默认分支后，选择以下任一入口：

1. **手动发布**：进入 GitHub **Actions → Release Windows application → Run workflow**，选择包含工作流的目标分支，输入 `0.0.1`（也接受 `v0.0.1`）。默认发布到 Releases；勾选 `draft` 则保留草稿。验证通过后，自动为所选提交建立 `v0.0.1` tag。
2. **通过 tag 发布**：在包含该工作流的目标提交上推送 tag：

```powershell
git switch main
git pull --ff-only
git tag v0.0.1
git push origin v0.0.1
```

下一版使用新的版本号，例如 `0.0.2` / `v0.0.2`。每段数字为 0–65535，不接受 0.0.0、前导零或预发布后缀。工作流把输入统一写入构建副本：应用/原生引擎使用 `X.Y.Z`，程序集、可执行文件和 MSIX 使用 `X.Y.Z.0`，附件文件名使用 `X.Y.Z`。仓库中的开发版本不会因此自动提交或改变。

构建步骤包含锁依赖测试、自包含打包、安装/卸载校验与 SHA-256 校验；发布步骤先建立草稿，再上传完整附件，最后公开，失败不会公开半套附件。Release 附件包括：

- `FrameTrail-X.Y.Z-x64-Setup.exe`、`FrameTrail-X.Y.Z-x64-portable.zip`
- `SHA256SUMS.txt`、`release-manifest.json`、`payload-manifest.json`、`nuget-dependencies.lock.json`
- `INSTALL.zh-CN.md`（同时作为 Release 正文）

构建任务只读，独立发布任务使用内置 `GITHUB_TOKEN` 的 `contents: write` 权限，不需要新增个人令牌或签名 secret。仓库需允许 Actions 运行；组织策略也需允许工作流申请该写权限。发布前再次核对 tag 指向、源 SHA 和附件；同版本已公开时会失败，请发布新版本。失败的同提交草稿可以重跑补全；不要移动已经公开的 tag。Actions 中额外保留 14 天构建与安装校验证据。

## 安装版的更新与保留

保存项目并关闭全部 FrameTrail 窗口后运行新版 Setup.exe。固定 AppId 会复用当前用户的安装位置和卸载入口；应用存在时安装和卸载会中止并提示先关闭，不会强制结束录制或导出。安装器只管理自身载荷，不删除安装目录之外的项目、偏好和预设。用户工程应保持在默认视频目录或其他外部目录。

当前更新方式为用户从 Releases 手动下载安装，不包含后台自动更新。关于页的系统更新查询沿用已有 MSIX 路径；EXE/便携版的版本下载以 Releases 为准。跨版本升级保留、失败恢复和干净 Windows 的完整矩阵仍待验收。

## 构建与校验

```powershell
.\scripts\package.cmd -OutputDirectory artifacts\packages\candidate
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\candidate -SmokeTest
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\candidate -WorkflowTest
```

`package.cmd` 默认执行锁依赖 Release 构建及测试，然后自包含发布；`-SkipBuild` 跳过前置全量构建/测试，但 publish 仍编译应用。产物是 x64 ZIP、MSIX、逐文件 `payload-manifest.json`、NuGet 锁文件和 `SHA256SUMS.txt`。清单记录源提交和是否有未提交修改；自包含运行时、编译 XAML、原生 DLL、字体和许可都进入载荷。

需要本地安装器时先准备固定版本 Inno Setup，然后增加 `-BuildInstaller`：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/setup-installer-tool.ps1
.\scripts\package.cmd -BuildInstaller -OutputDirectory artifacts\packages\installer-candidate
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify-installer.ps1 -PackageDirectory artifacts\packages\installer-candidate -InstallDirectory "$PWD\artifacts\installer-test" -SmokeTest
```

安装验证使用新的专用目录，发现当前用户已安装 FrameTrail 时拒绝覆盖。它会安装、校验并卸载测试副本；`-SmokeTest` 需要已登录的桌面，会启动测试窗口。Inno Setup 7.1.0 从官方发布下载，固定 SHA-256 并检查发布者签名，以便携模式放入被忽略的 `.tools/`，不注册系统编译器。编译器仅用于构建，不是应用运行依赖。

应用图标统一使用 `src/FrameTrail.App/Assets/Brand/FrameTrail.Icon.v1.ico`，嵌入 EXE，并随 build/publish 复制供各窗口加载。`src/FrameTrail.App/PackageAssets/` 包含从同版 PNG 生成的 57 个透明安装包图标，覆盖基准尺寸、125%–400% 缩放和明暗无底板资源。打包时由 `packaging/priconfig.xml` 将构建 PRI 中的图标和嵌入 XAML 一并合入包身份对应的 `resources.pri`；保留 `FrameTrail.App.pri` 供 WinUI 加载。更新原图后可通过 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/generate-icon-assets.ps1` 重建这些 PNG。见 [图标验证](validation/app-icon.md)。

`verify-package` 逐项校验载荷和两个压缩包的 SHA-256；`-SmokeTest` 清除子进程的开发运行时变量和 PATH，执行 100 次引擎生命周期检查，并核对实际加载的运行时与产品模块位置。`-WorkflowTest` 在同一候选上录制两段真实屏幕区域、保存/重开、剪辑/撤销/重做并导出 MP4。UI 检查属于应用内调用，不等同于系统鼠标/跨 DPI 可访问性验收。

## 签名与证书

默认发布者为 `CN=FrameTrail Development`，MSIX 明确标记为未签名开发候选。正常侧载需要可信签名。已有 CurrentUser/My 代码签名证书时可传入 `-Publisher` 和 `-SigningCertificateThumbprint`；发布者必须与证书 Subject 完全一致。脚本不会创建、导出私钥或自动导入信任。签名后调用 SignTool 验证证书信任链；开发证书未获测试机信任时验证会失败。

目前没有正式证书条件，签名分支尚未实测。签名服务/时间戳、secret 管理以及到期和撤销策略仍待配置；不得把私钥或密码加入仓库。当前 CI 不读取签名凭据；EXE 明确保持未签名状态。

## App Installer 与手动更新

有真实 HTTPS 分发目录后，可给打包命令增加 `-UpdateBaseUri <以斜杠结尾的 HTTPS 目录>`，生成同目录 `FrameTrail.appinstaller`。主包身份、版本、架构与 MSIX 相同；路径包含凭据、query 或 fragment 会被拒绝。不配置来源时不生成描述文件，也没有默认线上地址。

描述文件不含 `OnLaunch`、后台任务或降级设置。关于页的更新按钮只调用系统查询接口，报告状态后由用户保存、结束任务、退出应用并从原来源手动安装；没有自动下载、退出或强制关闭逻辑。服务器证书、下载失败、升级保留和安装失败提示仍要在真实签名/安装环境验证。

## 安装、保留与卸载策略

MSIX 由 Windows 安装到受管理目录；用户工程保持在用户视频目录或自主选择的外部位置。代码无卸载删除工程操作。包私有偏好/诊断数据受 Windows 包生命周期影响；卸载前可主动导出。便携版需退出后自行替换/删除解压目录。更新和卸载前不得有活动录制或导出。

正式候选发布检查还包括：稳定 Windows 11 干净机器、签名信任、首次安装和启动、真实媒体流程、上一版升级、失败提示、工程与偏好保留、卸载、SmartScreen 行为、许可与校验留存、0010 发布阻断关闭。当前均不能仅依据开发机启动标记完成。

## 技术依据

- [Microsoft：创建包签名证书](https://learn.microsoft.com/windows/msix/package/create-certificate-package-signing)
- [Microsoft：MSIX 签名](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide)
- [Microsoft：App Installer 描述文件](https://learn.microsoft.com/en-us/windows/msix/app-installer/how-to-create-appinstaller-file)
- [Microsoft：App Installer 更新设置](https://learn.microsoft.com/en-us/windows/msix/app-installer/update-settings)
