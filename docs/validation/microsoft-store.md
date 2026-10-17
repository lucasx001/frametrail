# Microsoft Store MSIX 验证

日期：2026-10-17。分支：`codex/github-releases`，在 PR #4 上补充 Store 分发支持。用户已明确尚未注册商店应用，因此没有真实 Partner Center Identity。

- `test-store.ps1`：19 项通过，包括 0.0.1/0.0.2、跨主版本升级、最大数字边界、身份缺失/格式/占位拒绝和 XML 转义。
- `test-store-package.ps1`：4 项通过。篡改身份、版本、载荷清单或签名标记后，即使重新生成外层 SHA-256，也拒绝验证。
- 既有 32 项 Release 元数据和 11 个隔离发布场景通过；actionlint 1.7.12 与 PowerShell Parser 无错误。
- 第一轮本机产物：`artifacts/packages/store-test-01/FrameTrail-0.4.0-x64-Store-TEST-ONLY.msix`，约 102 MiB，597 项载荷、包身份、版本、unsigned 状态与 SHA-256 block map 通过。来源为之前已验证的自包含候选；MakeAppx 和 MakePRI 实际执行成功。
- MakePRI dump 验证资源主映射为 `FrameTrail.StorePackagingTest`，保留编译 XAML 与图标资源，原候选身份和 PRI 不被改写。
- 最终本机交付为 `artifacts/packages/store-test-02/FrameTrail-0.4.0-x64-Store-TEST-ONLY.msix`：先用当前打包脚本重新生成 `store-source-02`，597 文件、MSIX/ZIP 和清理开发环境后的实际 WinUI/100 次生命周期检查通过，再生成 Store MSIX并通过相同的 597 文件和 4 项篡改拒绝检查。

产品版本维持 0.4.0，测试 Store 版本 1.4.0.0。包内/外元数据均标记测试身份；未创建或导入证书，未安装 MSIX，未注册应用、配置 Partner Center 或上传审核。

真实商店身份、已安装 MSIX 的采集/编辑/导出及升级、稳定干净 Windows、WACK 和商店认证仍待验收；没有把本机结构/归档校验作为这些项目的完成证据。云端执行结果以 [PR #4](https://github.com/lucasx001/frametrail/pull/4) 的最新检查与 Actions 的 Store 附件为准。
