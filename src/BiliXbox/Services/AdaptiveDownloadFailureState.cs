using System;
using System.Collections.Generic;
using System.Globalization;

namespace BiliXbox.Services
{
    // Called under the source's event lock. Success on another track/range
    // cannot establish that the failed video initialization/segment recovered.
    internal sealed class AdaptiveDownloadFailureState
    {
        internal const int MaximumRequests = 1024, MaximumFailures = 128;
        private readonly Dictionary<string, Request> _requests = new Dictionary<string, Request>();
        private readonly Queue<Request> _order = new Queue<Request>();
        private readonly HashSet<string> _failed = new HashSet<string>();
        private bool _unmatchedFailure;
        internal bool Pending => _unmatchedFailure || _failed.Count != 0;
        internal int Count => _failed.Count + (_unmatchedFailure ? 1 : 0);

        internal void Requested(string id, string resource, int type, ulong? offset, ulong? length)
        {
            var request = new Request { Id = id, Key = string.IsNullOrEmpty(resource) ? null : resource + "\n"
                + type.ToString(CultureInfo.InvariantCulture) + "\n" + offset?.ToString(CultureInfo.InvariantCulture)
                + "\n" + length?.ToString(CultureInfo.InvariantCulture) };
            while (_order.Count >= MaximumRequests)
            {
                var old = _order.Dequeue();
                Request current;
                if (_requests.TryGetValue(old.Id, out current) && ReferenceEquals(old, current)) _requests.Remove(old.Id);
            }
            _requests[id] = request;
            _order.Enqueue(request);
        }

        internal void Failed(string id)
        {
            Request request;
            if (!_requests.TryGetValue(id, out request)) { _unmatchedFailure = true; return; }
            if (request.Finished) return; // Late/duplicate notification.
            request.Finished = true;
            if (request.Key == null || _failed.Count >= MaximumFailures) _unmatchedFailure = true;
            else _failed.Add(request.Key);
        }

        internal void Completed(string id)
        {
            Request request;
            if (!_requests.TryGetValue(id, out request) || request.Finished) return;
            request.Finished = true;
            if (request.Key != null) _failed.Remove(request.Key);
        }

        internal void Cancelled(string id)
        {
            if (_requests.TryGetValue(id, out var request)) request.Finished = true;
        }

        private sealed class Request { internal string Id, Key; internal bool Finished; }
    }
}
