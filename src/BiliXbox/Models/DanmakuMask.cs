using System;

namespace BiliXbox.Models
{
    public sealed class DanmakuMaskInfo
    {
        public string Url { get; set; } = "";
        public double Fps { get; set; } = 24;
    }

    public sealed class DanmakuMaskFrame
    {
        public TimeSpan Time { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Svg { get; set; } = "";
    }
}
