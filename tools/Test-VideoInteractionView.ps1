#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ('BiliXbox.InteractionViewTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskSource = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BiliXbox.Models;
using BiliXbox.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
'@
foreach ($taskFile in @('src\BiliXbox\Models\ApiModels.cs', 'src\BiliXbox\Models\VideoInteractionModels.cs',
    'src\BiliXbox\Controls\VideoInteractionSession.cs', 'src\BiliXbox\Controls\VideoInteractionView.cs',
    'src\BiliXbox\Controls\VideoInteractionView.Data.cs', 'tools\tests\VideoInteractionView.Stubs.cs',
    'tools\tests\VideoInteractionView.Tests.cs')) {
    $taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $taskRoot $taskFile)), '(?m)^using [^;]+;\r?\n', '')
}
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3', '/nowarn:0067'
[BiliXbox.InteractionViewTests.Tests]::Run().GetAwaiter().GetResult()
