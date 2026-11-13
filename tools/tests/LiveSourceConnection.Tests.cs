namespace BiliXbox.LiveSourceConnectionTests
{
    public static class Tests
    {
        private sealed class Result { internal int Disposed; }
        public static async Task Run()
        {
            int count = 0;
            Action<bool, string> check = (value, name) => { ++count; if (!value) throw new Exception(name); };
            int cancelled = 0; var first = new Result();
            var result = await LiveSourceConnectionGate.ConnectAsync(Task.FromResult(first), CancellationToken.None,
                TimeSpan.FromSeconds(2), () => ++cancelled, value => ++value.Disposed);
            check(ReferenceEquals(result, first) && cancelled == 0 && first.Disposed == 0, "Timely source result transfers exactly once to player owner");
            var late = new Result(); var pending = new TaskCompletionSource<Result>();
            var disposed = new TaskCompletionSource<bool>(); var watch = Stopwatch.StartNew();
            try
            {
                await LiveSourceConnectionGate.ConnectAsync(pending.Task, CancellationToken.None, TimeSpan.FromMilliseconds(30),
                    () => ++cancelled, value => { ++value.Disposed; disposed.TrySetResult(true); });
                throw new Exception("Expected bounded timeout");
            }
            catch (TimeoutException) { }
            check(watch.Elapsed < TimeSpan.FromSeconds(2) && cancelled == 1, "Hung native source cannot block fallback indefinitely");
            check(!pending.Task.IsCompleted && late.Disposed == 0, "Timeout can complete even if native task ignores cancellation");
            pending.SetResult(late);
            check(await Task.WhenAny(disposed.Task, Task.Delay(2000)) == disposed.Task && late.Disposed == 1, "Native result arriving after timeout is disposed once");
            var cancelScope = new CancellationTokenSource(); pending = new TaskCompletionSource<Result>(); late = new Result(); disposed = new TaskCompletionSource<bool>();
            var cancelledAttempt = LiveSourceConnectionGate.ConnectAsync(pending.Task, cancelScope.Token, TimeSpan.FromSeconds(30),
                () => ++cancelled, value => { ++value.Disposed; disposed.TrySetResult(true); });
            cancelScope.Cancel();
            try { await cancelledAttempt; throw new Exception("Expected room cancellation"); } catch (OperationCanceledException) { }
            check(cancelled == 2, "Changing room cancels in-flight native operation immediately");
            pending.SetResult(late);
            check(await Task.WhenAny(disposed.Task, Task.Delay(2000)) == disposed.Task && late.Disposed == 1, "Cancelled old room cannot retain a late media source");
            cancelScope = new CancellationTokenSource(); cancelScope.Cancel(); first = new Result();
            try
            {
                await LiveSourceConnectionGate.ConnectAsync(Task.FromResult(first), cancelScope.Token,
                    TimeSpan.FromSeconds(2), () => ++cancelled, value => ++value.Disposed);
                throw new Exception("Expected cancellation before transfer");
            }
            catch (OperationCanceledException) { }
            check(first.Disposed == 1, "A result racing room cancellation is disposed before any player transfer");
            try
            {
                await LiveSourceConnectionGate.ConnectAsync(Task.FromException<Result>(new InvalidOperationException("bad manifest")),
                    CancellationToken.None, TimeSpan.FromSeconds(2), () => ++cancelled, value => ++value.Disposed);
                throw new Exception("Expected original manifest error");
            }
            catch (InvalidOperationException ex) { check(ex.Message == "bad manifest", "Immediate source failure preserves error and permits next candidate"); }
            pending = new TaskCompletionSource<Result>();
            try
            {
                await LiveSourceConnectionGate.ConnectAsync(pending.Task, CancellationToken.None, TimeSpan.FromMilliseconds(30),
                    () => { throw new ObjectDisposedException("operation"); }, value => ++value.Disposed);
                throw new Exception("Expected timeout despite failed native Cancel");
            }
            catch (TimeoutException) { check(true, "Native Cancel failure cannot prevent bounded fallback"); }
            pending.SetException(new Exception("late native failure"));
            // Exercise sequential real-candidate policy: a timed-out first
            // candidate has no return path capable of replacing the backup.
            pending = new TaskCompletionSource<Result>(); first = new Result(); var backup = new Result();
            try
            {
                await LiveSourceConnectionGate.ConnectAsync(pending.Task, CancellationToken.None, TimeSpan.FromMilliseconds(30),
                    () => { }, value => ++value.Disposed);
            }
            catch (TimeoutException) { }
            result = await LiveSourceConnectionGate.ConnectAsync(Task.FromResult(backup), CancellationToken.None,
                TimeSpan.FromSeconds(2), () => { }, value => ++value.Disposed);
            pending.SetResult(first);
            for (int i = 0; i < 10 && first.Disposed == 0; i++) await Task.Delay(10);
            check(ReferenceEquals(result, backup) && backup.Disposed == 0 && first.Disposed == 1, "Only current fallback may reach player while old result is cleaned");
            Console.WriteLine("PASS: " + count + " production live source timeout, cancellation, late disposal and fallback assertions (offline).");
        }
    }
}
