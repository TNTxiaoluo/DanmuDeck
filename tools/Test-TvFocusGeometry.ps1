#requires -Version 7.0
<#
.SYNOPSIS
Checks the real focus-scale calculation against the reserved horizontal gutter.
.DESCRIPTION
Compiles the unchanged production helper. Includes poster and wide dynamic-row
sizes, float-rounding boundaries and unmeasured controls. This does not establish
native XAML clipping, shadow rendering or Xbox focus behavior.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ('BiliXbox.TvFocusGeometryTests.Tests' -as [type]) { throw 'Run in a fresh PowerShell process.' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\BiliXbox\Controls\TvFocusGeometry.cs'))
$source += [Environment]::NewLine + @'
namespace BiliXbox.TvFocusGeometryTests
{
    using System;
    using System.Collections.Generic;

    public static class Tests
    {
        private static int _checks;

        public static string Run()
        {
            _checks = 0;
            foreach (double width in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity,
                0d, -0d, -1d, -282d, -double.MaxValue })
                Check(Controls.TvFocusGeometry.GetScale(width) == 1f,
                    "Unknown or nonpositive layout width must not scale the control.");

            foreach (double width in new[] { 1d, 68d, 246d, 282d })
                Check(Controls.TvFocusGeometry.GetScale(width) == 1.06f,
                    "A poster-sized control should retain the shared six-percent focus lift.");

            float dynamicScale = Controls.TvFocusGeometry.GetScale(1136d);
            Check(dynamicScale > 1f && dynamicScale < 1.03f,
                "A full-width dynamic card must use a smaller lift than a poster.");
            Check(Controls.TvFocusGeometry.GetScale(1200d) < Controls.TvFocusGeometry.GetScale(282d),
                "Wider cards must not inherit the poster's percentage expansion.");

            var widths = new List<double> { double.Epsilon, 1e-300, 1e-100, 0.00001,
                1, 246, 282, 400, 1000, 1136, 1200, 4096, 100000, 1000000,
                240000000, 1e20, 1e100, double.MaxValue };
            var random = new Random(18417);
            for (int index = 0; index < 512; ++index)
                widths.Add(Math.Pow(10d, random.NextDouble() * 16d - 4d));

            // These widths make the twelve-pixel limit land near a float ULP.
            // A nearest-float cast used without downward correction can exceed it.
            foreach (float scale in new[] { 1.0000001192092896f, 1.0000019073486328f,
                1.0001f, 1.001f, 1.01f, 1.02f, 1.04f, 1.06f })
            {
                double width = 24d / ((double)scale - 1d);
                widths.Add(width);
                widths.Add(width * (1d - 1e-10));
                widths.Add(width * (1d + 1e-10));
            }

            widths.Sort();
            float previous = float.PositiveInfinity;
            foreach (double width in widths)
            {
                float scale = Controls.TvFocusGeometry.GetScale(width);
                Check(!float.IsNaN(scale) && !float.IsInfinity(scale), "Focus scale must be finite.");
                Check(scale >= 1f, "Focus must never shrink a card.");
                Check(scale <= 1.06d, "Focus must not exceed the shared poster scale.");
                double sideExpansion = ((double)scale - 1d) * width / 2d;
                Check(sideExpansion <= 12d, "Actual float expansion must remain inside the twelve-pixel gutter.");
                Check(scale <= previous, "Focus scale must not increase as a card becomes wider.");
                previous = scale;
            }
            return "PASS: " + _checks + " production focus-scale and gutter-bound checks; native visual rendering is not simulated.";
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            ++_checks;
        }
    }
}
'@
Add-Type -TypeDefinition $source -CompilerOptions '/langversion:7.3'
[BiliXbox.TvFocusGeometryTests.Tests]::Run()
