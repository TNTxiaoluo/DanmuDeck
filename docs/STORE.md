# 商店身份与上传包

开发测试签名和正式商店身份采用独立流程。开源代码不包含发布者的私钥、真实商店关联文件或既有商店上传包。

本次正式送审版本为 `1.0.2`，包版本为 `1.0.2.0`；商店说明可直接使用 [1.0.2 发布说明](RELEASE-NOTES-1.0.2.md)中的简版。此前开发测试改进已整合到该正式版本，详见 [更新日志](../CHANGELOG.md)。正式版本号表示本次提交使用的标识，不表示审核已通过。

## 配置自己的产品身份

在自己的 Partner Center 产品中取得以下公开字段：

- `Package/Identity/Name`
- `Package/Identity/Publisher`
- `Package/Properties/PublisherDisplayName`

复制示例文件：

```powershell
Copy-Item -LiteralPath tools/Package.StoreAssociation.example.xml -Destination tools/Package.StoreAssociation.xml
```

将复制文件中的三项空字段填为自己产品的实际值。`tools/Package.StoreAssociation.xml` 是本机发布配置，已由 `.gitignore` 排除；示例模板随源码分发。

## 生成 Store 上传包

按 [BUILDING.md](BUILDING.md) 安装工具后执行：

```powershell
pwsh -NoProfile -File tools/Build-StoreUpload.ps1 -StoreAssociationPath tools/Package.StoreAssociation.xml -Version 1.0.2.0
```

工具使用微软 MSBuild 的 `StoreUpload` 模式，在隔离副本中覆盖商店身份、显示名称和版本，输出到 `artifacts/StoreUpload/`。上传容器扩展名由工具链确定为 `.msixupload` 或 `.appxupload`，内部包含应用包和调试符号。构建同时校验包身份、版本、品牌资源、符号及数据块。

可用 `-PrepareOnly` 只生成构建计划，或用 `-Version` 指定本次包版本。版本采用 `Major.Minor.Build.0`；正式上传包的界面版本不带测试 `b` 后缀，后续开发测试交付则保留该后缀。字母后缀不写入 APPX/MSIX 的数字包版本。`-MSBuildPath` 可指定自己的 MSBuild，`-OutputDirectory` 必须位于项目工作区内。

Store 上传构建不使用本机开发证书签名，签名由商店流程处理。脚本不执行上传或发布。开发者模式安装请使用 [BUILDING.md](BUILDING.md) 中的已签名测试包流程。

## Xbox HEVC / 4K / HDR10

当前清单声明 `hevcPlayback`，对应现有 Xbox HEVC、4K/HDR10 播放路径。此受限能力需要按微软流程申请审批，并说明实际使用场景；源码能够构建和开发者模式测试，不代表商店已批准该能力。

如果决定制作不申请该能力的版本，需要同时检查画质菜单、轨道筛选与兼容回退，保持界面与实际可用的播放能力一致。设备查询支持和商店能力批准是不同条件。

参考微软文档：

- [应用产品标识](https://learn.microsoft.com/en-us/windows/apps/publish/view-app-identity-details)
- [UWP 应用打包](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps)
- [受限能力审批流程](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/app-capability-declarations#restricted-capability-approval-process)
- [Xbox 4K 视频播放](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/hevc-xbox)
