using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BiliXbox.Services
{
    // The HTTP response exposes its validated head immediately. Out-of-order
    // future pieces wait in a bounded window; a slow tail cannot hold the head.
    internal sealed class OrderedRangeReadStream : Stream
    {
        internal const int ReadAheadBytes = 2 * 1024 * 1024;
        private readonly object _gate = new object();
        private readonly Dictionary<int, byte[]> _pieces = new Dictionary<int, byte[]>();
        private readonly CancellationTokenSource _abort;
        private readonly CancellationToken _token;
        private readonly SemaphoreSlim _reader = new SemaphoreSlim(1, 1);
        private TaskCompletionSource<bool> _changed = Signal();
        private readonly int _length;
        private readonly WaitStatistics _waits;
        private readonly Action<string> _trace;
        private int _position, _currentIndex;
        private int _lastGapPosition = -1;
        private byte[] _current;
        private bool _disposed, _complete;
        private Exception _error;
        internal CancellationToken ProducerToken => _token;
        internal OrderedRangeReadStream(int length, CancellationToken request, CancellationToken owner,
            WaitStatistics waits = null, Action<string> trace = null)
        {
            _length = length; _waits = waits; _trace = trace;
            _abort = CancellationTokenSource.CreateLinkedTokenSource(request, owner); _token = _abort.Token;
        }
        private static TaskCompletionSource<bool> Signal()
            => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Pulse() { var old = _changed; _changed = Signal(); old.TrySetResult(true); }

        internal async Task WaitForWindowAsync(int offset, CancellationToken token)
        {
            bool waiting = false;
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    _token.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                    if (offset <= _position + ReadAheadBytes)
                    {
                        if (waiting) TraceCursor("window-ready", offset);
                        return;
                    }
                    if (!waiting) TraceCursor("window-wait", offset);
                    waiting = true;
                    wait = _changed.Task;
                }
                await WaitAsync(wait, token, forWindow: true).ConfigureAwait(false);
            }
        }
        internal Task AddAsync(int offset, byte[] bytes, CancellationToken token)
        {
            lock (_gate)
            {
                _token.ThrowIfCancellationRequested(); token.ThrowIfCancellationRequested();
                if (offset < _position || bytes == null || bytes.Length > _length - offset || _pieces.ContainsKey(offset))
                    throw new IOException("加速分段顺序或长度无效。");
                _pieces.Add(offset, bytes);
                TraceCursor("write", offset, bytes.Length);
                Pulse();
            }
            return Task.CompletedTask;
        }
        internal void Complete(Exception error = null)
        {
            lock (_gate)
            {
                _complete = true; _error = error;
                TraceCursor(error == null ? "producer-complete" : "producer-aborted", _position);
                Pulse();
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
            using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _token))
            {
                await _reader.WaitAsync(read.Token).ConfigureAwait(false);
                try
                {
                    while (true)
                    {
                        Task wait;
                        lock (_gate)
                        {
                            read.Token.ThrowIfCancellationRequested();
                            if (_disposed) throw new ObjectDisposedException(nameof(OrderedRangeReadStream));
                            if (count == 0 || _position == _length) return 0;
                            if (_current == null && _pieces.TryGetValue(_position, out _current))
                            { _pieces.Remove(_position); _currentIndex = 0; }
                            if (_current != null)
                            {
                                int copied = Math.Min(count, _current.Length - _currentIndex);
                                int from = _position;
                                bool first = _currentIndex == 0;
                                Buffer.BlockCopy(_current, _currentIndex, buffer, offset, copied);
                                _currentIndex += copied; _position += copied;
                                if (_currentIndex == _current.Length) _current = null;
                                if (first || _current == null) TraceCursor("consume", from, copied);
                                Pulse(); return copied;
                            }
                            if (_error != null) throw new IOException("加速下载中断，正在使用原生线路恢复。", _error);
                            if (_complete) throw new IOException("加速下载在媒体范围结束前中断。");
                            if (_trace != null && _lastGapPosition != _position)
                            {
                                _lastGapPosition = _position;
                                TraceCursor("read-wait", _position);
                            }
                            wait = _changed.Task;
                        }
                        await WaitAsync(wait, read.Token, forWindow: false).ConfigureAwait(false);
                    }
                }
                finally { _reader.Release(); }
            }
        }

        // Positions are relative to the parent HTTP Range. The cursor is the
        // next byte requested by the media HTTP stack, upstream of decoding.
        private void TraceCursor(string phase, int offset, int count = 0)
        {
            if (_trace == null) return;
            int contiguousEnd = _position + (_current == null ? 0 : _current.Length - _currentIndex);
            int writtenEnd = contiguousEnd;
            foreach (var piece in _pieces) writtenEnd = Math.Max(writtenEnd, piece.Key + piece.Value.Length);
            while (_pieces.TryGetValue(contiguousEnd, out var segment) && segment.Length > 0) contiguousEnd += segment.Length;
            _trace(phase + " offset=" + offset + " bytes=" + count + " consumedNext=" + _position
                + " contiguousEnd=" + contiguousEnd + " writtenEnd=" + writtenEnd
                + " gap=" + (contiguousEnd < writtenEnd ? contiguousEnd.ToString() : "none"));
        }
        private async Task WaitAsync(Task signal, CancellationToken token, bool forWindow)
        {
            long start = _waits?.Begin(forWindow) ?? 0;
            var canceled = Signal();
            try
            {
                using (token.Register(() => canceled.TrySetCanceled()))
                { await await Task.WhenAny(signal, canceled.Task).ConfigureAwait(false); }
            }
            finally { _waits?.End(forWindow, start); }
        }

        internal sealed class WaitStatistics
        {
            private int _windowWaiters, _readWaiters;
            private long _windowTicks, _readTicks;
            internal int WindowWaiters => Volatile.Read(ref _windowWaiters);
            internal int ReadWaiters => Volatile.Read(ref _readWaiters);
            internal long WindowMilliseconds => Interlocked.Read(ref _windowTicks) * 1000 / Stopwatch.Frequency;
            internal long ReadMilliseconds => Interlocked.Read(ref _readTicks) * 1000 / Stopwatch.Frequency;
            internal long Begin(bool forWindow)
            {
                if (forWindow) Interlocked.Increment(ref _windowWaiters);
                else Interlocked.Increment(ref _readWaiters);
                return Stopwatch.GetTimestamp();
            }
            internal void End(bool forWindow, long start)
            {
                long ticks = Stopwatch.GetTimestamp() - start;
                if (forWindow)
                {
                    Interlocked.Add(ref _windowTicks, ticks);
                    Interlocked.Decrement(ref _windowWaiters);
                }
                else
                {
                    Interlocked.Add(ref _readTicks, ticks);
                    Interlocked.Decrement(ref _readWaiters);
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    TraceCursor("stream-close", _position);
                    _disposed = true; _pieces.Clear(); _current = null; Pulse();
                }
                _abort.Cancel(); _abort.Dispose();
            }
            base.Dispose(disposing);
        }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get { lock (_gate) return _position; } set { throw new NotSupportedException(); } }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
