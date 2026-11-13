#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = [IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Services/LoginCaptchaBridge.cs'))
$taskHarness = [IO.File]::ReadAllText((Join-Path $taskRoot 'tools/tests/LoginCaptcha.Tests.cs'))
$taskJson = [Newtonsoft.Json.Linq.JObject].Assembly.Location
$taskRefs = @(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName) + @($taskJson)
Add-Type -TypeDefinition ($taskSource + [Environment]::NewLine + $taskHarness) -ReferencedAssemblies $taskRefs -CompilerOptions '/langversion:7.3', '/nowarn:1701'
[BiliXbox.LoginCaptchaTests.Checks]::Run()
$taskPagePath = Join-Path $taskRoot 'artifacts/captcha-fixture-page.html'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskPagePath)) | Out-Null
[IO.File]::WriteAllText($taskPagePath, [BiliXbox.LoginCaptchaTests.Checks]::Page(), [Text.UTF8Encoding]::new($false))
$taskNode = (Get-Command node -ErrorAction Stop).Source
& $taskNode (Join-Path $taskRoot 'tools/tests/LoginCaptchaPage.Tests.js') $taskPagePath
if ($LASTEXITCODE -ne 0) { throw 'Verification page behavior checks failed.' }
