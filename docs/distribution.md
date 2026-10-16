# 候选分发与更新

M4 当前为开发候选阶段。正式对外分发前需完成 [0010](0010-reliability-performance.md) 与 [0011](0011-distribution.md) 的未勾选验收项。

## 构建与校验

```powershell
.\scripts\package.cmd -OutputDirectory artifacts\packages\candidate
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\candidate -SmokeTest
.\scripts\verify-package.cmd -PackageDirectory artifacts\packages\candidate -WorkflowTest
```

`package.cmd` 默认执行锁依赖 Release 构建及测试，然后自包含发布；`-SkipBuild` 跳过前置全量构建/测试，但 publish 仍编译应用。产物是 x64 ZIP、MSIX、逐文件 `payload-manifest.json`、NuGet 锁文件和 `SHA256SUMS.txt`。清单记录源提交和是否有未提交修改；自包含运行时、编译 XAML、原生 DLL、字体和许可都进入载荷。

`verify-package` 逐项校验载荷和两个压缩包的 SHA-256；`-SmokeTest` 清除子进程的开发运行时变量和 PATH，执行 100 次引擎生命周期检查，并核对实际加载的运行时与产品模块位置。`-WorkflowTest` 在同一候选上录制两段真实屏幕区域、保存/重开、剪辑/撤销/重做并导出 MP4。UI 检查属于应用内调用，不等同于系统鼠标/跨 DPI 可访问性验收。

## 签名与证书

默认发布者为 `CN=FrameTrail Development`，MSIX 明确标记为未签名开发候选。正常侧载需要可信签名。已有 CurrentUser/My 代码签名证书时可传入 `-Publisher` 和 `-SigningCertificateThumbprint`；发布者必须与证书 Subject 完全一致。脚本不会创建、导出私钥或自动导入信任。签名后调用 SignTool 验证证书信任链；开发证书未获测试机信任时验证会失败。

本轮没有证书条件，签名分支尚未实测。正式流水线还需配置签名服务/时间戳与 secret 管理，并检查到期和撤销策略；不得把私钥或密码加入仓库。当前 CI 只生成未签名候选，不读取签名凭据。

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
