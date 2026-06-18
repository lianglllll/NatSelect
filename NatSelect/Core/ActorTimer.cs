namespace NatSelect.Core;

/// <summary>
/// Actor 定时器
/// 支持一次性和循环定时，与 Actor 生命周期绑定
/// </summary>
public sealed class ActorTimer : IAsyncDisposable
{
    private readonly Timer _timer;
    private readonly Func<ValueTask> _callback;
    private bool _isDisposed;

    public int Id { get; }
    public bool IsRepeating { get; }
    public int IntervalMs { get; }

    internal ActorTimer(int id, int intervalMs, bool repeat, Func<ValueTask> callback)
    {
        Id = id;
        IntervalMs = intervalMs;
        IsRepeating = repeat;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));

        // 创建定时器
        _timer = new Timer(OnTimerTick, null, intervalMs, repeat ? intervalMs : Timeout.Infinite);
    }

    private void OnTimerTick(object? state)
    {
        if (_isDisposed) return;

        // 异步执行回调
        _ = Task.Run(async () =>
        {
            try
            {
                await _callback();
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[ActorTimer] Timer {Id} callback failed", Id);
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
