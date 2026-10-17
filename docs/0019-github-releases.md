# 0019 — GitHub Releases 与 Windows 安装包

状态：待验收（2026-10-17，本地实现与安装测试完成；云端和首次公开版本单独记录）。依赖：0011。规格依据：[spec.md](spec.md)。

## 目的与实现

从 GitHub Actions 构建带版本号的 Windows 安装包，并附加到对应 GitHub Release。支持推送 `vX.Y.Z` tag 和手动输入版本号；手动运行默认发布，也可保留草稿。版本只在构建工作区写入，不要求每次先手工修改仓库中的开发版本。

1. 校验三段数字版本，将其统一写入应用程序集、原生引擎、Windows manifest、MSIX 和安装器；只调整 NuGet 锁文件中的本地项目版本。
2. 锁依赖构建和测试，生成自包含 Windows x64 载荷、便携 ZIP、MSIX 以及 Inno Setup 安装器。
3. Setup.exe 按当前用户安装，无需管理员权限；包含卸载入口、开始菜单和可选桌面快捷方式。应用运行期间通过命名 mutex 拒绝安装/卸载，不强制结束媒体任务。
4. 校验载荷、安装与卸载，然后整理公共附件：Setup.exe、便携 ZIP、清单、安装说明和 SHA-256。未签名 MSIX 留在 CI 候选，不附加到公共 Release。
5. 发布任务在再次校验版本、源 SHA 和附件后创建草稿、上传完整附件、最后公开。只允许重试草稿，拒绝覆盖已公开版本或使用指向其他提交的 tag。
6. 构建任务仅需读取仓库，独立发布任务获得 `contents: write`，使用内置 `GITHUB_TOKEN`；无需个人访问令牌。

## 验收 Todo

- [x] 版本 0.0.1 / 0.0.2 与四段 Windows 版本一致，非法版本拒绝
- [x] 发布附件缺失、篡改、重复、路径越界和来源不符时拒绝
- [x] 工作流通过 actionlint，脚本通过 PowerShell 语法检查
- [x] 本机生成 Setup.exe，完成载荷校验、安装、启动与卸载
- [x] 同版本覆盖安装、运行标记阻止安装/卸载、外部文件保留
- [ ] GitHub Actions 执行记录
- [ ] 首次正式指定版本的 Release 公开及附件下载验证
- [ ] 上一版本升级、干净稳定 Windows 和代码签名矩阵

操作说明见 [distribution.md](distribution.md)，执行证据见 [验证记录](validation/github-releases.md)。本任务不将硬件、长时同步或签名验收视为已完成。

Microsoft Store 提交需求由 [0020](0020-microsoft-store.md) 补充：配置真实商店身份后，发版工作流额外生成单独的 Store Actions 附件；尚未注册时由独立工作流/CI 生成明确标记的测试包。它不进入普通用户安装附件，也不需要自购签名证书。
