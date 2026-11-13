# 构建

以下命令在项目根目录的 PowerShell 中执行。应用显示名称为“弹幕舱”，解决方案、程序集和源码目录沿用 `BiliXbox`。

## 环境

- Windows x64。
- PowerShell 7，可使用 `pwsh` 命令。
- Visual Studio 2022，安装 UWP C# 开发工具、x64 构建组件及 .NET Native 工具链。
- Windows SDK `10.0.19041.0`。项目最低目标系统版本为 `10.0.17763.0`。
- 首次还原 NuGet 依赖需要网络。

打开 `BiliXbox.sln`，选择 `Release | x64`。Xbox 测试使用 Release 的 .NET Native 构建。工具脚本优先检测本机工具缓存，再通过 Visual Studio 安装信息查找 MSBuild；也可用 `-MSBuildPath` 指定自己的安装路径。

## 检查源码

```powershell
pwsh -NoProfile -File tools/Check-Source.ps1
```

此命令检查 XML、XAML 事件入口和图标；具备 Roslyn 时还会检查 C# 7.3 语法。完整编译仍由 Visual Studio/MSBuild 完成。

## 生成未签名 APPX

```powershell
pwsh -NoProfile -File tools/Build-Appx.ps1 -Configuration Release
```

输出位于 `artifacts/AppPackages/`，构建日志位于 `artifacts/logs/`。该命令默认生成未签名侧载包，适合编译验证。

## 生成已签名 Xbox 测试包

```powershell
pwsh -NoProfile -File tools/Build-XboxTest.ps1 -Configuration Release -OutputDirectory artifacts/AppPackages/LocalTest -DistributionPath artifacts/DanmuCabin-LocalTest.zip
```

首次执行会在 `artifacts/dev-certificate/` 生成与当前清单 Publisher 匹配的本机开发证书，后续执行会校验并复用它。ZIP 包含签名 APPX、所需 x64 依赖、公开 `.cer`、校验值和安装说明，可通过 Xbox 开发者模式的 Device Portal 部署。脚本本身只构建和打包。

本机生成的签名通常与其他开发者的签名不同。覆盖安装需要相匹配的包身份和签名；请在自己的开发设备或测试环境中安装。

`.pfx` 包含私钥，保留在本机；公开分发仅使用生成的测试 ZIP 或其 APPX、依赖和 `.cer`。源码仓库及开源 ZIP 不包含签名证书、SDK、NuGet 缓存或既有安装包。

## 图标资源

仓库已包含可直接编译的 PNG，无需先重建图标。`BrandIcon.png` 和透明 `BrandMark.png` 是母图，其他 PNG 为尺寸及 DPI 派生资源。

需要重新生成时，在 Windows 上安装 FFmpeg 并使 `ffmpeg.exe` 可从 PATH 找到，然后执行：

```powershell
pwsh -NoProfile -File tools/Generate-BrandAssets.ps1 -PngEncoder (Get-Command ffmpeg.exe -ErrorAction Stop).Source
```

`-PngEncoder` 也接受自备 FFmpeg 可执行文件的路径。FFmpeg 仅用于派生 PNG 编码和体积优化，应用运行时不依赖它。

商店身份和上传包流程见 [STORE.md](STORE.md)，测试范围见 [TESTING.md](TESTING.md)。
