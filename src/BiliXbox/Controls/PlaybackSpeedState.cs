using System;
using System.Collections.Generic;

namespace BiliXbox.Controls
{
    internal sealed class PlaybackSpeedState
    {
        internal static readonly IReadOnlyList<double> Rates = Array.AsReadOnly(new[] { .5, .75, 1.0, 1.25, 1.5, 2.0 });
        internal double Selected { get; private set; } = 1;
        internal bool Temporary { get; private set; }
        private bool _releaseRequired = true;
        internal double Effective => Temporary ? 2 : Selected;
        internal void Select(double rate)
        {
            bool valid = false; foreach (var option in Rates) if (rate == option) valid = true;
            if (!valid) throw new ArgumentOutOfRangeException(nameof(rate));
            Selected = rate;
        }
        internal bool Sample(double trigger, bool eligible)
        {
            bool previous = Temporary;
            if (double.IsNaN(trigger) || double.IsInfinity(trigger)) trigger = 0;
            if (trigger <= .25) _releaseRequired = false;
            if (!eligible) { Temporary = false; _releaseRequired = trigger > .25; }
            else if (_releaseRequired) Temporary = false;
            else if (Temporary) { if (trigger <= .25) Temporary = false; }
            else if (trigger >= .65) Temporary = true;
            return previous != Temporary;
        }
        internal void Cancel() { Temporary = false; _releaseRequired = true; }
    }
}
