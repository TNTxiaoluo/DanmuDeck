using System;
using System.Collections.Generic;

namespace BiliXbox.Controls
{
    // Keep recent arrivals under overload. Never hold the queue lock while
    // calling the renderer, dispatcher or a native socket.
    internal sealed class LiveMessageQueue<T>
    {
        private readonly object _gate = new object();
        private readonly Queue<T> _items = new Queue<T>();
        internal const int Capacity = 128;
        internal int Count { get { lock (_gate) return _items.Count; } }
        internal void Enqueue(T item)
        {
            lock (_gate)
            {
                if (_items.Count == Capacity) _items.Dequeue();
                _items.Enqueue(item);
            }
        }
        internal bool TryDequeue(out T item)
        {
            lock (_gate)
            {
                if (_items.Count == 0) { item = default(T); return false; }
                item = _items.Dequeue(); return true;
            }
        }
        internal void Clear() { lock (_gate) _items.Clear(); }
    }

    internal static class LiveResourceCleanup
    {
        // A native detach/dispose failure must not strand the other resources
        // or escape through a routed B/Click event and terminate the app.
        internal static void Run(Action<Exception> report, params Action[] steps)
        {
            foreach (var step in steps)
            {
                try { step?.Invoke(); }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                { try { report?.Invoke(ex); } catch (Exception) { } }
            }
        }
    }
}
