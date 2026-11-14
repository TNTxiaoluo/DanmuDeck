#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskModel=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Models/ApiModels.cs'))
$taskPolicy=[IO.File]::ReadAllText((Join-Path $taskRoot 'src/BiliXbox/Controls/PlaybackQualityEntitlement.cs'))
$taskTests=@'
namespace BiliXbox.Controls
{
    public static class QualityEntitlementTests
    {
        public static string Run()
        {
            int checks = 0;
            foreach (int tier in new[] {112,116,120,125,126,127})
            {
                var quality = new BiliXbox.Models.PlaybackQuality { Id = tier };
                if (PlaybackQualityEntitlement.Badge(quality,false,false) != "需要登录") throw new Exception("Anonymous tier did not require login"); ++checks;
                if (PlaybackQualityEntitlement.Badge(quality,true,false) != "需要大会员") throw new Exception("VIP tier did not require membership"); ++checks;
                if (PlaybackQualityEntitlement.Badge(quality,true,true) != "") throw new Exception("Premium falsely blocked"); ++checks;
            }
            foreach (int tier in new[] {16,32,64})
            {
                if (PlaybackQualityEntitlement.Badge(new BiliXbox.Models.PlaybackQuality {Id=tier},false,false) != "") throw new Exception("Free tier falsely blocked"); ++checks;
            }
            var hd = new BiliXbox.Models.PlaybackQuality {Id=80};
            if (PlaybackQualityEntitlement.Badge(hd,false,false) != "需要登录" || PlaybackQualityEntitlement.Badge(hd,true,false) != "") throw new Exception("1080P login gate wrong"); ++checks;
            var custom = new BiliXbox.Models.PlaybackQuality {Id=74, RequiresPremium=true};
            if (PlaybackQualityEntitlement.Badge(custom,true,false) != "需要大会员") throw new Exception("Server VIP metadata ignored"); ++checks;
            custom.RequiresPremium=false;custom.RequiresLogin=true;
            if (PlaybackQualityEntitlement.Badge(custom,false,false) != "需要登录") throw new Exception("Server login metadata ignored"); ++checks;
            return "PASS: " + checks + " production quality entitlement checks.";
        }
    }
}
'@
$taskSource="using System;`nusing System.Collections.Generic;`nusing BiliXbox.Models;`n"
$taskSource += [regex]::Replace($taskModel,'(?m)^using [^;]+;\r?\n','')+"`n"+[regex]::Replace($taskPolicy,'(?m)^using [^;]+;\r?\n','')+$taskTests
Add-Type -TypeDefinition $taskSource -CompilerOptions '/langversion:7.3'
[BiliXbox.Controls.QualityEntitlementTests]::Run()
