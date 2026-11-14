# 弹幕舱

<img src="src/BiliXbox/Assets/BrandMark.png" width="96" alt="弹幕舱图标" />

面向 Xbox 大屏和手柄操作的第三方 Bilibili UWP 客户端，使用 C# 7.3 / XAML 开发。

项目独立于哔哩哔哩和微软。内部解决方案、程序集和命名空间保留 `BiliXbox`，应用显示名称为“弹幕舱”。当前正式版本为 **1.0.2**，包版本为 `1.0.2.0`，整合了此前开发测试阶段的改进。[本次发布说明](docs/RELEASE-NOTES-1.0.2.md)可用于商店送审和 GitHub Release。

除明确准备微软商店发布外，后续开发测试版本的界面版本号使用 `b` 后缀，例如 `1.0.2b`。APPX/MSIX 包清单继续使用四段数字版本；显示后缀与包版本是不同字段。

## 功能

- **浏览**：直播、推荐、热门、动画、影视分区；关键词相关视频、BV/链接和用户搜索。分区切换保留已加载的页码、滚动与焦点，明确刷新时才重新换批。
- **播放**：原生 DASH / MP4 播放、分 P、画质选择、缓冲速度和进度显示；分别检查片源解码和实际输出能力。支持已验证 4K HDR10 源的系统 SDR 色调映射，并将特殊帧率来源明确标为兼容试播。
- **音频与缓冲**：播放设置中选择真实标准、杜比或 Hi-Res 无损音轨，按账号权限及系统解码器提供；高级音轨失败先回退同画质标准音轨。仅显示片源已知的采样率、位深等信息，不保证设备输出保持原始格式。按真实连续可播缓存达到 3 秒恢复，保留主动暂停与片尾处理。
- **弹幕**：滚动、顶部和底部弹幕，显示区域、透明度、密度、字号、屏蔽规则；滚动和固定弹幕独立布局，固定弹幕显示在上层；支持平台提供的人物防挡遮罩。
- **评论**：分页、楼中楼回复、图片全屏、表情、头像、等级、点赞和点踩；按接口实际返回的数据展示。
- **用户主页**：投稿和动态浏览；支持联合投稿，保留视频真实主 UP。
- **个人内容**：播放历史、收藏、追番追剧，以及视频点赞、投币、收藏和三连。登录后实际播放视频时同步观看进度，暂停、结束和离开时补充上报。
- **直播**：房间浏览、播放、实时弹幕、聊天和礼物交互。
- **账号**：二维码登录，以及密码、短信验证入口；会话使用本机用户绑定加密保存。
- **手柄**：左摇杆/方向键导航、A 确认、B 返回、LB/RB 分区切换、右摇杆滚动；原位调用系统键盘。

平台接口、账户权限、网络条件和设备能力会影响功能可用性。4K/HDR10 需要兼容的 Xbox、显示设备、系统配置及真实播放源；Xbox 的 `hevcPlayback` 还是需要商店单独审批的受限能力。硬件测试和纯代码测试的结果应分别看待。

## 下载

<a href="https://apps.microsoft.com/detail/9N4MBQJC77DD">
  <img
    src="https://get.microsoft.com/images/en-us%20dark.svg"
    alt="Get it from Microsoft Store"
    width="200"
  />
</a>

## 快速开始

需要 Windows、**Visual Studio 2022 UWP 开发工具**（C# UWP、.NET Native）、**Windows 10 SDK 10.0.19041.0** 和 **PowerShell 7**。

1. 打开 `BiliXbox.sln`，还原 NuGet 依赖。
2. 选择 `Debug / x64`，在 Windows 上调试。
3. 用 `Release / x64` 生成 Xbox 测试包。以下命令在仓库根目录运行：

```powershell
# 检查源码和 XAML 结构
pwsh -NoProfile -File tools/Check-Source.ps1

# 构建，并在本机生成/复用自己的开发证书进行签名
pwsh -NoProfile -File tools/Build-XboxTest.ps1
```

测试 ZIP 默认生成于 `artifacts/BiliXbox-Xbox-Test.zip`，包含 APPX、所需依赖、公开证书和安装说明。维护者的签名私钥、登录数据、历史安装包以及本机工具链不随源码分发。新生成的测试证书与维护者的原证书不同。

完整流程见[构建与安装](docs/BUILDING.md)。商店打包需要填写你自己的 Partner Center 标识，见[商店构建与受限能力](docs/STORE.md)。

## 开发与测试

```powershell
pwsh -NoProfile -File tools/Test-CreatorAccess.ps1
pwsh -NoProfile -File tools/Test-ReleaseVersion.ps1
pwsh -NoProfile -File tools/Test-CreatorProfileRefresh.ps1
pwsh -NoProfile -File tools/Test-Search.ps1
pwsh -NoProfile -File tools/Test-HomeFeedCache.ps1
pwsh -NoProfile -File tools/Test-PlaybackHistory.ps1
pwsh -NoProfile -File tools/Test-PlaybackQuality.ps1
pwsh -NoProfile -File tools/Test-NativeFrameRateValidation.ps1
pwsh -NoProfile -File tools/Test-PlaybackAudio.ps1
pwsh -NoProfile -File tools/Test-PlaybackAudioUi.ps1
pwsh -NoProfile -File tools/Test-PlaybackBuffering.ps1
```

这些检查通过替身验证生产代码的逻辑，默认不使用真实账户或发布内容，不能代替 Xbox 真机的播放、焦点和 HDMI 检查。显式的在线探测、原生运行探针和真实交互应按各脚本说明单独运行。详见[测试说明](docs/TESTING.md)及[手柄操作](docs/CONTROLS.md)。

4K 选项缺失、编码筛选、本地日志位置和 Xbox 验证步骤见[播放诊断](docs/PLAYBACK-DIAGNOSTICS.md)。

仓库包含 GitHub Actions 配置。需要运行环境具备项目要求的 UWP 工具链；CI 产物默认未签名，应以实际运行记录判断结果。

## 目录

```text
.github/workflows/          GitHub Actions 配置
src/BiliXbox/               UWP 源码、XAML 和图标
tools/                     构建、检查和打包脚本
tools/tests/               确定性测试替身与回归用例
docs/                      构建、测试、操作和商店说明
BiliXbox.sln               Visual Studio 解决方案
LICENSE                    MIT 许可证
THIRD-PARTY-NOTICES.txt     第三方依赖声明
```

`.tools/`、`artifacts/`、`bin/`、`obj/`、本机商店关联文件和证书由 `.gitignore` 排除。已有图标直接随源码提供，正常构建不需要重新生成图标。

## 重新导出源码 ZIP

```powershell
pwsh -NoProfile -File tools/Package-Source.ps1
```

导出脚本只收集公开源码和文档，校验文件哈希并生成独立清单。正式 `1.0.2` 的默认 ZIP 名称为 `DanmuCabin-1.0.2-source.zip`。整理好的源码文件夹与 ZIP 内部项目目录使用同一布局：GitHub 仓库根目录直接包含 `README.md`、`BiliXbox.sln`、`src/`、`tools/`、`docs/`，同时保留 `.github/` 和 `.gitignore`。上传该项目目录内的文件即可浏览源码，ZIP 可另作为 Release 的源码附件。

源码包排除维护者的签名材料、账号数据、真实商店关联信息、构建产物及本机工具链；使用自己的证书和产品身份构建。构建与导出脚本不自动上传 GitHub 或提交商店。

## 贡献与许可证

欢迎提交 Issue 和 Pull Request。请先阅读[贡献说明](CONTRIBUTING.md)；修复记录见[更新日志](CHANGELOG.md)。报告问题时请提供设备、版本和复现步骤，隐藏账号凭据和个人资料。

本项目采用 [MIT 许可证](LICENSE)。第三方依赖按各自的许可证使用，见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
