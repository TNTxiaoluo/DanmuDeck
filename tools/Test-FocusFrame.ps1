#requires -Version 7.0
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskSource=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/TvFocusEffects.cs'))
$taskMethods=''
foreach($taskName in @('UpdateFocusRing','FindTagged')){
    $taskMatch=[regex]::Match($taskSource,'(?m)^        private static \w+ '+$taskName+'\([^\r\n]*\)\s*\{')
    if(-not $taskMatch.Success){throw "Missing focus method: $taskName"}
    $taskBrace=$taskSource.IndexOf('{',$taskMatch.Index);$taskEnd=$taskBrace+1;$taskDepth=1
    while($taskDepth -gt 0){if($taskSource[$taskEnd] -eq '{'){$taskDepth++};if($taskSource[$taskEnd] -eq '}'){$taskDepth--};$taskEnd++}
    $taskMethods+=$taskSource.Substring($taskMatch.Index,$taskEnd-$taskMatch.Index)+[Environment]::NewLine
}
$taskHarness=@'
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
namespace Windows.UI.Xaml {
    public class DependencyObject { public List<DependencyObject> Children=new List<DependencyObject>(); }
    public class FrameworkElement:DependencyObject { public object Tag;public double Opacity; }
}
namespace Windows.UI.Xaml.Controls { public class Control:FrameworkElement {} public class Border:Control {} }
namespace Windows.UI.Xaml.Media { public static class VisualTreeHelper {public static int GetChildrenCount(DependencyObject root)=>root.Children.Count;public static DependencyObject GetChild(DependencyObject root,int i)=>root.Children[i];} }
namespace BiliXbox.Controls {
    public static class FocusFrameTests {
        /* METHODS */
        public static string Run(){
            var card=new Control();var host=new Control();var ring=new Border {Tag="TvFocusRing"};card.Children.Add(host);host.Children.Add(ring);
            UpdateFocusRing(card,true);if(ring.Opacity!=1)throw new Exception("Focused card has no white frame");
            UpdateFocusRing(card,false);if(ring.Opacity!=0)throw new Exception("Lost/recycled focus leaves a ghost frame");
            UpdateFocusRing(card,true);if(ring.Opacity!=1)throw new Exception("Returning to a recycled card must show frame again");
            UpdateFocusRing(new Control(),true);UpdateFocusRing(null,false);
            return "PASS: production focus frame appears, clears, reappears and tolerates missing/unrealized templates.";
        }
    }
}
'@
Add-Type -TypeDefinition $taskHarness.Replace('/* METHODS */',$taskMethods) -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.FocusFrameTests]::Run()
[xml]$taskXaml=Get-Content -LiteralPath (Join-Path $taskRoot 'src/BiliXbox/App.xaml') -Raw
foreach($taskKey in @('TvCardItemStyle','TvDynamicItemStyle')){
    $taskStyle=$taskXaml.SelectNodes("//*[local-name()='Style']")|Where-Object {$_.GetAttribute('Key','http://schemas.microsoft.com/winfx/2006/xaml') -eq $taskKey}
    $taskRing=$taskStyle.SelectSingleNode(".//*[@Tag='TvFocusRing']")
    if(-not $taskRing -or $taskRing.BorderBrush -ne 'White' -or [double]$taskRing.BorderThickness -lt 3){throw "$taskKey lacks a visible white focus border"}
}
'PASS: both production card templates contain white focus borders independent of composition shadows. Native rendering still requires Xbox validation.'
