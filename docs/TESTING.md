# 测试

测试脚本位于 `tools/`，替身和用例位于 `tools/tests/`。从项目根目录使用 PowerShell 7，每个脚本启动独立的 `pwsh` 进程，避免重复加载同名测试类型。

## 日常检查

```powershell
pwsh -NoProfile -File tools/Check-Source.ps1
pwsh -NoProfile -File tools/Test-ReleaseVersion.ps1
pwsh -NoProfile -File tools/Test-ApiLogic.ps1
pwsh -NoProfile -File tools/Test-Creator.ps1
pwsh -NoProfile -File tools/Test-CreatorAccess.ps1
pwsh -NoProfile -File tools/Test-CreatorProfileRefresh.ps1
pwsh -NoProfile -File tools/Test-Search.ps1
pwsh -NoProfile -File tools/Test-GamepadInput.ps1
pwsh -NoProfile -File tools/Test-HomeFeedCache.ps1
pwsh -NoProfile -File tools/Test-HomeFeedFocus.ps1
pwsh -NoProfile -File tools/Test-LiveNavigation.ps1
pwsh -NoProfile -File tools/Test-PlaybackHistory.ps1
pwsh -NoProfile -File tools/Test-PlaybackQuality.ps1
pwsh -NoProfile -File tools/Test-PlaybackCapability.ps1
pwsh -NoProfile -File tools/Test-MultiTrackPlayback.ps1
pwsh -NoProfile -File tools/Test-ToneMappedPlayback.ps1
pwsh -NoProfile -File tools/Test-NativeFrameRateValidation.ps1
pwsh -NoProfile -File tools/Test-QualityEntitlements.ps1
pwsh -NoProfile -File tools/Test-PlaybackAudio.ps1
pwsh -NoProfile -File tools/Test-PlaybackAudioUi.ps1
pwsh -NoProfile -File tools/Test-PlaybackBuffering.ps1
pwsh -NoProfile -File tools/Test-PlaybackTransportIntegration.ps1
pwsh -NoProfile -File tools/Test-PlaybackDiagnostics.ps1
pwsh -NoProfile -File tools/Test-Comments.ps1
```

按改动选择相关脚本；[GitHub Actions 工作流](../.github/workflows/build.yml)列出了持续集成使用的其他检查。部分脚本需要 Windows、Visual Studio 或必选的文件路径，具体以脚本顶部参数为准。

大部分默认测试编译实际生产代码，并以受控 HTTP、存储、控件或任务替身检查解析、分页、取消、账号边界和焦点状态。它们不会证明 Xbox 的实际手柄映射、系统键盘、HDMI 输出、硬件解码帧率或视觉效果。

4K、HDR、编码回退与 Xbox 实机复测步骤见[播放诊断](PLAYBACK-DIAGNOSTICS.md)。

`Test-NativeFrameRateValidation.ps1` 编译实际原生帧率校验分支，用媒体轨道替身检查接口声明与原生帧率分离、尺寸验证、视频轨道回退，以及换源/取消后旧任务不能修改当前播放器。它不执行实际硬件解码；62.5fps 接口声明的 60fps 基础能力查询还由能力与多轨道测试覆盖。

`Test-PlaybackCapability.ps1` 与 `Test-PlaybackDisplay.ps1` 还检查 3996/4096×2160 DCI 片源与 3840×2160 UHD 输出的分离：实际解码尺寸不能改写，SDR/HDR10 能力不能混用，8K 等超出有界例外的尺寸不能借用 UHD 支持结果，实际解码拒绝仍应保留。

1.0.7 的显示测试还覆盖 HDR10 片源的已验证 4K SDR 色调映射：保持源为 HDR10/10bit，只申请 SDR 输出，不启用 HDR10 状态；不一致的档位、尺寸、位深或缺失 SDR 证明必须拒绝。高帧率声明只有带显式试播标记与 60fps 基准参数才可申请不超过 60Hz 的输出，原始声明不改写。`Test-PlaybackDisplayIntegration.ps1` 检查回退提示仍区分 HDR10 源与 SDR 输出；取消和默认显示恢复也必须保持。

1.0.6 的 Hi-Res 回归沿用 `Test-PlaybackAudio.ps1` 与 `Test-PlaybackAudioUi.ps1`：检查实际 FLAC 音轨识别、会员与解码器限制、同视频标准音轨回退，以及切换期间的位置/暂停意图和旧请求隔离。替身中的音频参数只验证解析与显示，不证明 Xbox 实际以相同采样率、位深或逐位不变输出；这些仍需目标设备和音频链路验证。

缓冲恢复回归同时执行 `Test-PlaybackBuffering.ps1`、`Test-PlaybackPipelineRecovery.ps1` 与 `Test-PlaybackTransportIntegration.ps1`。测试覆盖主动等待 2.5 秒缓存超过原生停滞期限时不重建媒体源，缓存达到 3 秒后恢复而不额外暂停首次原生缓冲，以及原生恢复交接真正卡死时仍保留一次有限重建；不会通过修改下载速度来假定缓存已可播放。

## 可选公开接口复测

以下命令会联网读取普通公开数据。B 站接口可能变更或限制请求，失败不应被当成空列表或虚构成功结果。

```powershell
pwsh -NoProfile -File tools/Test-Search.ps1 -Live -Keyword Xbox -Mode Video -Pages 1 -Summary
pwsh -NoProfile -File tools/Test-Creator.ps1 -PublicMid 2 -PublicPages 1
```

`Test-Creator.ps1` 的 `-PublicMid` 可替换为要复测的公开用户 UID；`-PublicPages` 限制读取页数。这类探测从空的内存会话开始，不加载本机登录文件。请勿将真实 Cookie、密码、短信验证码或带凭据的请求内容加入测试 fixture。

## 安装包检查

- `Test-Package.ps1`：验证已签名原生 x64 APPX、公开证书、依赖和分发 ZIP。必须提供现有绝对路径参数 `-PackagePath`、`-PublicCertificatePath`、`-DistributionZipPath`。
- `Test-StoreUpload.ps1`：按指定身份和版本检查 Store 上传包、符号和数据块。
- `Test-StoreUploadTools.ps1`：检查商店构建工具的参数、隔离副本和身份覆盖行为，不提交商店。

安装包签名和内容校验通过后，仍需在目标设备验证启动、播放、导航、暂停/恢复和退出。

## 本机运行时探针

`Invoke-UwpRuntimeProbe.ps1`、`Probe-CreatorUi.ps1` 及名称含 `Runtime` 的脚本用于更具体的 Windows/UWP 环境检查。部分探针需要本机 Windows SDK、编译器、预先生成的包或本机工具缓存，也可能临时注册应用或启动本地测试服务。请先阅读相应脚本的参数及清理行为。

可选的 Hi-Res 原生探针读取本机有权使用的 FLAC-in-MP4 样本，静音验证 Windows 解码器查询、原生音频参数与播放时钟。它使用本机 Windows SDK 和仓库工具缓存，不属于纯替身测试：

```powershell
pwsh -NoProfile -File tools/Test-HiResRuntime.ps1 -SamplePath 'C:/Samples/authorized-flac-in-mp4.mp4'
```

开发验证中，4 秒静音合成样本的 96kHz、24bit、双声道原生参数与播放时钟检查通过。这说明本机 Windows 的该条解码路径可运行，不代表 Xbox 结果，也不证明真实输出链路逐位不变。

这些探针属于可选验证工具，不是所有贡献都必须执行的 CI 前置条件。开源 ZIP 不附带它们曾使用的本机 SDK、工具缓存或运行记录。
