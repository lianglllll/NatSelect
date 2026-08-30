namespace NatSelect.Core;

/// <summary>
/// Actor 定时器
/// 支持一次性和循环定时，与 Actor 生命周期绑定。
/// 到期时向 Actor 邮箱投递 TimerTickMessage，由 ActorScheduler 的 Worker
/// 统一调度执行回调，保证回调与消息处理串行（线程安全）。
/// </summary>
public sealed class ActorTimer : IAsyncDisposable
{
    private readonly Timer _timer;
    private readonly Func<ValueTask> _callback;
    private readonly IActorSystem _system;
    private readonly ActorRef _self;
    private bool _isDisposed;

    public int Id { get; }
    public bool IsRepeating { get; }
    public int IntervalMs { get; }

    internal ActorTimer(int id, int intervalMs, bool repeat, Func<ValueTask> callback,
        IActorSystem system, ActorRef self)
    {
        Id = id;
        IntervalMs = intervalMs;
        IsRepeating = repeat;
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _self = self;

        // 底层 Timer 仅负责到期通知，不执行业务回调
        _timer = new Timer(state => _ = TickAsync(), null, intervalMs, repeat ? intervalMs : Timeout.Infinite);
    }

    private async Task TickAsync()
    {
        if (_isDisposed) return;

        try
        {
            await _system.SendAsync(_self, new TimerTickMessage(Id) { Sender = _self });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[ActorTimer] Timer {Id} failed to enqueue tick", Id);
        }
    }

    /// <summary>
    /// 执行定时器回调（在 Actor 调度上下文中调用，与消息处理串行）
    /// </summary>
    internal async ValueTask ExecuteCallbackAsync()
    {
        if (_isDisposed) return;

        try
        {
            await _callback();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[ActorTimer] Timer {Id} callback failed", Id);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_isDisposed) return ValueTask.CompletedTask;
        _isDisposed = true;

        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 定时器到期消息（系统消息，由调度器投递到 Actor 邮箱）
/// </summary>
public sealed class TimerTickMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public int TimerId { get; }

    public TimerTickMessage(int timerId)
    {
        TimerId = timerId;
    }
}
