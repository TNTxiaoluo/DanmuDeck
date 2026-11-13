#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ('BiliXbox.Controls.CommentCard' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$taskCard = [IO.File]::ReadAllText((Join-Path $taskRoot 'src\BiliXbox\Controls\CommentCard.cs'))
$taskStart = $taskCard.IndexOf('public bool TryActivate(Control control)', [StringComparison]::Ordinal)
$taskEnd = $taskCard.IndexOf('public void RefreshReaction()', $taskStart, [StringComparison]::Ordinal)
if ($taskStart -lt 0 -or $taskEnd -le $taskStart) { throw 'Production comment action methods were not found.' }
$taskSource = @'
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
'@
$taskSource += [Environment]::NewLine + [regex]::Replace([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'tests\CommentCardNavigation.Tests.cs')), '(?m)^using [^;]+;\r?\n', '')
$taskSource += "`nnamespace BiliXbox.Controls { public sealed partial class CommentCard { " + $taskCard.Substring($taskStart, $taskEnd - $taskStart) + " } }`n"
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.CommentCard]::RunTests()
