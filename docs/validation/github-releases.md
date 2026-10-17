# GitHub Release 与安装器验证

日期：2026-10-17。分支：`codex/github-releases`。当前状态见 [0019](../0019-github-releases.md)。

## 本地验证

- `scripts/test-release.ps1`：29 项通过，覆盖 0.0.1、0.0.2、版本上限、非法输入、本地项目锁依赖更新与外部依赖保持，以及发布附件版本/源提交/完整性保护。
- PowerShell 7 下另有 11 个隔离发布场景通过：首次发布、轻量/附注 tag、来源不符、已公开版本、草稿来源和重试、先草稿后公开、上传失败不得公开。测试替换 `gh` 为进程内假实现，不写 GitHub。另对真实仓库执行只读的 0.0.1 首次发布预检，通过。
- actionlint 1.7.12：全部工作流无错误；PowerShell Parser：脚本无语法错误。
- 锁依赖 Release 构建通过，7 组原生、201 项托管默认测试通过。
- 独立源码副本分别写入 0.0.1 与 0.0.2，应用的实际 NuGet locked restore 均通过，工作目录开发版本保持 0.4.0。
- 最终本机候选 `artifacts/packages/github-release-02/`：597 文件、MSIX/ZIP 逐项 SHA-256 通过；清除开发 PATH/运行时变量后，实际 WinUI 启动与 100 次引擎生命周期检查通过。
- Setup.exe 约 72 MiB，便携 ZIP 约 100 MiB；安装器实际完成当前用户安装、同版本覆盖安装、597 文件校验、已安装应用启动、卸载注册和文件清理。模拟运行标记时安装和卸载均拒绝，安装目录之外的测试文件保留。安装/卸载日志和 `installer-validation/result.json` 保留在同一候选目录。
- 公共附件整理到 `artifacts/release-local-02/`，7 个附件的来源、版本、清单和 SHA-256 再次校验通过，不含未签名 MSIX；本地产物标记 `sourceDirty: true`，不代表已经公开的版本。

构建日志保留在 `artifacts/github-release-build.log`，打包日志在 `artifacts/github-release-package-02.log`。第一轮发现并修复 Inno PE 版本字符串的尾部空格检查问题；最终验证使用第二轮产物。首次发布预检改用 tag ref API，处理不存在的 tag 和附注 tag，避免 commits API 对未知版本返回 422 的误判。

## 验证边界

运行环境为当前 Windows 11 build 26300 开发机。GitHub 云端执行、正式指定版本的公开 Release、浏览器下载、上一版升级、干净稳定 Windows 安装和 SmartScreen/商业签名行为分别记录，不能由本地构建结果推断通过。当前 EXE 安装器未签名，MSIX 仅供开发验证；公开附件默认不含未签名 MSIX。
