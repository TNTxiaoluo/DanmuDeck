using System;

namespace BiliXbox.Controls
{
    // This state outlives the visible dynamic detail. Cancelling navigation says
    // nothing about whether an HTTP POST has already reached the server.
    internal sealed class CreatorCommentDraftState
    {
        private long _generation;
        private string _submittedDraft;
        public string Draft { get; private set; } = "";
        public bool InFlight { get; private set; }
        public bool IsUncertain { get; private set; }
        public bool RetryArmed { get; private set; }
        public string Notice { get; private set; } = "";
        public void Edit(string text) { Draft = text ?? ""; RetryArmed = false; }
        public void DisarmRetry() { RetryArmed = false; }
        public bool ConfirmRetry()
        {
            if (InFlight || !IsUncertain) return false;
            RetryArmed = true; return true;
        }
        public long Begin()
        {
            if (InFlight || string.IsNullOrWhiteSpace(Draft) || (IsUncertain && !RetryArmed)) return 0;
            InFlight = true; IsUncertain = RetryArmed = false; Notice = "";
            _submittedDraft = Draft;
            return ++_generation;
        }
        public bool Complete(long generation, bool success, string notice)
        {
            if (generation == 0 || generation != _generation || !InFlight) return false;
            InFlight = false; IsUncertain = !success; RetryArmed = false;
            Notice = notice ?? "";
            if (success && string.Equals(Draft, _submittedDraft, StringComparison.Ordinal)) Draft = "";
            return true;
        }
    }
}
