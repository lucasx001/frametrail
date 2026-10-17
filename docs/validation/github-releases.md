# GitHub Release 与安装器验证

日期：2026-10-17。分支：`codex/github-releases`。当前状态见 [0019](../0019-github-releases.md)。

## 本地验证

- `scripts/test-release.ps1`：32 项通过，覆盖 0.0.1、0.0.2、版本上限、非法输入、本地项目锁依赖更新与外部依赖保持，以及发布附件版本/源提交/完整性保护。各段上限统一为 .NET AssemblyVersion 支持的 65534，避免接受了 MSIX 数字范围却无法编译程序集；已用实际 C# 编译核对边界。
- PowerShell 7 下另有 11 个隔离发布场景通过：首次发布、轻量/附注 tag、来源不符、已公开版本、草稿来源和重试、先草稿后公开、上传失败不得公开。测试替换 `gh` 为进程内假实现，不写 GitHub。另对真实仓库执行只读的 0.0.1 首次发布预检，通过。
- actionlint 1.7.12：全部工作流无错误；PowerShell Parser：脚本无语法错误。
- 锁依赖 Release 构建通过，7 组原生、201 项托管默认测试通过。
- 独立源码副本分别写入 0.0.1 与 0.0.2，应用的实际 NuGet locked restore 均通过，工作目录开发版本保持 0.4.0。
- 最终本机候选 `artifacts/packages/github-release-02/`：597 文件、MSIX/ZIP 逐项 SHA-256 通过；清除开发 PATH/运行时变量后，实际 WinUI 启动与 100 次引擎生命周期检查通过。
- Setup.exe 约 72 MiB，便携 ZIP 约 100 MiB；安装器实际完成当前用户安装、同版本覆盖安装、597 文件校验、已安装应用启动、卸载注册和文件清理。模拟运行标记时安装和卸载均拒绝，安装目录之外的测试文件保留。安装/卸载日志和 `installer-validation/result.json` 保留在同一候选目录。
- 公共附件整理到 `artifacts/release-local-02/`，7 个附件的来源、版本、清单和 SHA-256 再次校验通过，不含未签名 MSIX；本地产物标记 `sourceDirty: true`，不代表已经公开的版本。

构建日志保留在 `artifacts/github-release-build.log`，打包日志在 `artifacts/github-release-package-02.log`。第一轮发现并修复 Inno PE 版本字符串的尾部空格检查问题；最终验证使用第二轮产物。首次发布预检改用 tag ref API，处理不存在的 tag 和附注 tag，避免 commits API 对未知版本返回 422 的误判。

## 验证边界

首轮云端 [39015769914](https://github.com/lucasx001/frametrail/actions/runs/39015769914) 的工具准备、新发布脚本、原生 7 组和应用构建通过，但原有缓存逐出单测依赖连续写入文件的时间差，在 hosted CI 同一时钟刻度下出现排序并列。已为测试中的旧/活动/新文件设置明确时间戳，保留原有容量和保护断言；产品缓存逻辑未改动。修复后的 CI 状态以 PR 检查为准。

运行环境为当前 Windows 11 build 26300 开发机。GitHub 云端执行、正式指定版本的公开 Release、浏览器下载、上一版升级、干净稳定 Windows 安装和 SmartScreen/商业签名行为分别记录，不能由本地构建结果推断通过。当前 EXE 安装器未签名，MSIX 仅供开发验证；公开附件默认不含未签名 MSIX。

## 2026-10-17：首次发版预检退出码修复

[首次手动发版 39023768645](https://github.com/lucasx001/frametrail/actions/runs/39023768645) 在源提交 `2f8aa92`、版本 `0.0.1` 上打印预检通过后仍以 1 退出。原因是查不到尚未创建的 Release/tag 时，`gh api` 返回 404 和退出码 1；脚本将结果处理为不存在，却没有清除 `$LASTEXITCODE`。GitHub 的 PowerShell shell 会把该变量作为最终进程退出码。

- 在已处理的 404 分支把调用方的 `$global:LASTEXITCODE` 归零；真正的 API 错误继续抛出异常。
- 成功预检场景现在同时检查异常与调用方退出码。增加 401、403、503 场景，共 14 个发布/预检检查通过；既有 32 项版本/附件检查通过。
- 使用真实 GitHub CLI，在独立 `pwsh` 进程中执行与 Actions 相同的末尾退出码检查：旧脚本退出 1，修复脚本退出 0，两个版本均打印同一 `v0.0.1 @ 2f8aa92` 预检通过信息。结果保留在 `artifacts/test-results/release-preflight-process.json`，全程只读。
- Windows CI 新增真实 `GITHUB_TOKEN` + `-CheckOnly` 步骤，用单次运行的探测版本校验实际 Actions shell 退出状态，不创建 tag 或 Release。

修复合并后应通过 **Run workflow** 从包含修复的新提交发起 `0.0.1`，不要直接 Re-run 原失败记录；重新运行旧记录沿用原来的 SHA 和工作流代码。[GitHub 重跑规则](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)。
