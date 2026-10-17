# 0020 — Microsoft Store MSIX 提交包

状态：待验收（2026-10-17，打包实现及本机测试包完成；用户尚未注册商店应用）。依赖：0011、0019。

## 目的与实现

在现有 GitHub 安装器之外提供未签名的 Microsoft Store MSIX，由商店在通过认证后签名，用户无需购买代码签名证书。

- 使用 Partner Center 的 Identity/Name、Identity/Publisher、PublisherDisplayName；三个仓库变量缺失/错误时拒绝生成正式提交包。
- 提供独立 Store 工作流；既有 Release 工作流在配置商店身份后复用同一载荷额外生成 Store Actions 附件。
- Store 专用 payload 隔离身份及 PRI，产品版本 `X.Y.Z` 映射到 Store `(X+1).Y.Z.0`，兼容 0.x 产品版本并保持升级顺序。
- 尚未注册时使用固定测试身份，文件名、说明及元数据均标记 `TEST-ONLY`，不宣称可以提交或直接侧载。
- MakeAppx、逐文件 SHA-256、block map、真实 ZIP 中的身份和未签名状态检查；分支/PR CI 持续生成测试包。

## 验收 Todo

- [x] 19 项身份/版本检查及 4 项重算校验后的身份/版本/清单/签名篡改拒绝
- [x] 本地生成测试 MSIX，并验证 597 文件和新身份的 PRI
- [x] 独立工作流和现有发版工作流接入，actionlint 通过
- [x] Store MSIX 云端构建、校验和上传（实现提交 ec82949，[CI 记录](https://github.com/lucasx001/frametrail/actions/runs/39017559326)）
- [ ] 注册商店应用，按实际身份生成提交包
- [ ] 已安装 MSIX 的功能/升级、WACK、Partner Center 上传及认证

操作说明见 [Microsoft Store 分发](microsoft-store.md)，验证证据见 [Store 验证](validation/microsoft-store.md)。不会将结构校验通过等同于商店认证或上架成功。
