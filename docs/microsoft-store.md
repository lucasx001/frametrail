# Microsoft Store MSIX 分发

FrameTrail 提供独立的 Microsoft Store 提交包。**通过商店发布 MSIX 不需要购买代码签名证书**：应用通过认证后由 Microsoft 签名和分发。这个流程不需要 PFX、私钥或签名 secret，也不自动提交商店审核。[Microsoft 官方签名说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options)。

当前尚未注册商店应用，因此先提供带 `TEST-ONLY` 标记的打包测试包。它验证结构、资源和完整性，不能作为正式产品上传；注册后必须使用实际商店标识重新生成。

## 注册后配置三个标识

1. 登录 [Partner Center](https://partner.microsoft.com/dashboard)，完成 Windows 开发者账户准备，创建 **MSIX/PWA** 类型应用并预留可用名称。
2. 在应用的 **Product management → Product identity** 中复制下列三个值。大小写、空格和标点必须完全一致，不要填产品显示名称代替 Identity/Name。
3. 在 GitHub 仓库 **Settings → Secrets and variables → Actions → Variables** 添加：

| 仓库变量 | Partner Center 对应字段 |
|---|---|
| `STORE_PACKAGE_NAME` | `Package/Identity/Name` |
| `STORE_PUBLISHER` | `Package/Identity/Publisher`，通常为 `CN=...` |
| `STORE_PUBLISHER_DISPLAY_NAME` | `Package/Properties/PublisherDisplayName` |

这些值会进入公开的包清单，可用普通变量保存；不需要分享账户密码。没有配置时，普通 GitHub Release 仍生成 Setup.exe 和便携 ZIP；只配置部分值则立即报错，避免产生身份错误的提交包。

## 从 GitHub 构建和下载

工作流合并到默认分支后，有两个入口：

- **单独生成 MSIX**：Actions → **Build Microsoft Store MSIX** → Run workflow，填写产品版本，如 `0.0.1`。已注册时保持 `test_only` 关闭；尚未注册时开启它，获得明确标记的测试包。
- **与 GitHub 版本一起生成**：配置上述变量后，原来的 **Release Windows application** 在 tag 或手动发版时，会用同一份应用载荷额外生成 Store 包。

在运行详情的 **Artifacts** 下载 `FrameTrail-Microsoft-Store-...`，解压后得到 `.msix`、`store-manifest.json`、`STORE-SUBMISSION.md` 和 `SHA256SUMS.txt`。保留期为 14 天，提交前应自行保存。

正式文件名为 `FrameTrail-X.Y.Z-x64-Store.msix`；测试文件名额外含 `TEST-ONLY`。普通分支/PR CI 也会生成测试 MSIX，用于持续验证打包链路。

Store 上传包保存在维护者的 Actions 产物中。面向用户的 GitHub Releases 继续提供 Setup.exe/便携 ZIP；未签名的 Store 包不能作为普通用户双击侧载的安装包。商店正式上线后，用户从 Microsoft Store 获取 Microsoft 签名的版本。

## 产品版本与商店包版本

Microsoft Store 包要求首段大于 0，并将第四段保留为 0。为继续支持产品的 `0.0.1` / `0.0.2` 版本，采用固定映射：**Store 包版本 = `(产品 major + 1).minor.patch.0`**。

| 产品 / GitHub 版本 | Store MSIX 版本 |
|---|---|
| 0.0.1 | 1.0.1.0 |
| 0.0.2 | 1.0.2.0 |
| 0.4.0 | 1.4.0.0 |
| 1.0.0 | 2.0.0.0 |

所有主版本都加一，因此从 0.x 升到 1.x 时包版本保持递增。程序集、原生引擎和应用关于页保留产品版本；`store-manifest.json` 同时记录两种版本、商店身份、来源和签名状态。[官方包版本要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)。

## 本地打包

先按 [分发说明](distribution.md) 生成并校验自包含候选目录，再用它生成独立的 Store 载荷；原候选的 manifest、PRI 和安装器不被修改。

```powershell
# 尚未注册：先验证打包。不是可提交的商店身份。
.\scripts\package-store.cmd -PackageDirectory artifacts\packages\candidate -OutputDirectory artifacts\packages\store-test -TestOnly

# 注册后：用 Partner Center 的真实值替换三个参数，不能照抄占位字符串。
.\scripts\package-store.cmd -PackageDirectory artifacts\packages\candidate -OutputDirectory artifacts\packages\store-submission -PackageName "PartnerCenter中的IdentityName" -Publisher "CN=PartnerCenter中的Publisher" -PublisherDisplayName "PartnerCenter中的PublisherDisplayName"
```

输出目录必须是新目录。脚本先逐项检查来源载荷，复制到独立目录后写入商店 Identity 和版本，重新建立对应身份的 PRI，再用 MakeAppx 校验和封装。保留 .NET、Windows App SDK、编译 XAML、原生 DLL、Android 组件、图标和许可；校验压缩包中每个载荷文件与 SHA-256 block map。不会创建证书、安装信任或安装应用。

## 在 Partner Center 提交

1. 下载非 `TEST-ONLY` 的 `.msix`，核对 `store-manifest.json` 的三个标识与当前产品一致。
2. 先完成已安装 MSIX 的实际功能验证、跨版本升级和 Windows App Certification Kit 检查。这些与 MakeAppx 结构校验不同，当前尚未执行。
3. 在产品提交的 **Packages** 页面上传 `.msix`。单架构 x64 包属于支持的格式，无需为了上传而改后缀成 `.msixupload`。
4. 完成商店描述、截图、年龄分级、隐私政策链接和认证说明，再提交审核。现有 [隐私说明](privacy.md) 可作为填写依据，正式链接需公开可访问。
5. 说明 `runFullTrust` 的用途：原生屏幕/声音采集、D3D/Media Foundation 编辑导出、本地工程访问，以及用户开启 Android USB 功能时启动随包 ADB；麦克风、摄像头为用户选用的录制输入。

审核通过和正式上架由 Partner Center 决定。当前包保留 `runFullTrust`、`microphone` 与 `webcam` 声明，不扩大设备权限。[官方包要求](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements)。
