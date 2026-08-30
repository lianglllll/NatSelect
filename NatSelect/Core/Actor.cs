using NatSelect.Common;
using NatSelect.Logger;
using Serilog;
using System.Threading.Channels;

namespace NatSelect.Core;

public abstract class Actor : IAsyncDisposable
{
    private readonly ChannelReader<IAMessage> _reader;
    private readonly ChannelWriter<IAMessage> _writer;
    private bool _shouldStopDueToError;
    private volatile bool _disposed;
    private int _scheduled = 0; // 0 = not scheduled, 1 = scheduled (防止重复入队)
    private int _processing = 0; // 0 = 空闲, 1 = 执行中（防止并发执行流）
    private volatile Action? _pendingContinuation; // 挂起协程的续延（同一 Actor 至多一个）

    protected ILogger Log { get; }

    /// <summary>
    /// Actor 上下文
    /// </summary>
    public ActorContext Context { get; }

    // 调度器引用（由 ActorSystem 注入）
    internal ActorScheduler? Scheduler { get; set; }

    /// <summary>
    /// 是否已销毁
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 邮箱中是否还有待处理消息
    /// </summary>
    public bool HasPendingMessages => _reader.TryPeek(out _);

    protected Actor(ActorContext context, int mailboxCapacity = 1024)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        var channel = Channel.CreateBounded<IAMessage>(
            new BoundedChannelOptions(mailboxCapacity)
            {
                // 邮箱满时丢弃新消息（防雪崩）
                FullMode = BoundedChannelFullMode.DropWrite
            });
        _reader = channel.Reader;
        _writer = channel.Writer;

        Log = NSLogger.ForContext(context);

        // 注意：不再自动启动 ProcessMessagesAsync，由调度器管理
    }

    /// <summary>
    /// 投递消息到邮箱，并通知调度器
    /// </summary>
    internal async ValueTask TellAsync(IAMessage msg)
    {
        if (_disposed) return;

        await _writer.WriteAsync(msg);

        // 协程挂起中不入队：恢复后由 Worker 收尾兜底重新入队
        if (!HasPendingContinuation)
            Scheduler?.Schedule(this);
    }

    /// <summary>
    /// 尝试标记为已调度（原子操作，防止重复入队）
    /// </summary>
    internal bool TryMarkScheduled()
    {
        return Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0;
    }

    /// <summary>
    /// 清除调度标记（处理完消息后调用）
    /// </summary>
    internal void ClearScheduled()
    {
        Interlocked.Exchange(ref _scheduled, 0);
    }

    /// <summary>
    /// 尝试开始执行（原子操作，防止并发执行流）
    /// </summary>
    internal bool TryBeginProcessing()
    {
        return Interlocked.CompareExchange(ref _processing, 1, 0) == 0;
    }

    /// <summary>
    /// 结束执行，释放执行流独占权
    /// </summary>
    internal void EndProcessing()
    {
        Interlocked.Exchange(ref _processing, 0);
    }

    /// <summary>
    /// 是否存在挂起的协程续延
    /// </summary>
    public bool HasPendingContinuation => _pendingContinuation != null;

    /// <summary>
    /// 取出挂起协程的续延并清空槽位（由 Worker 调用）
    /// </summary>
    internal bool TryTakeContinuation(out Action continuation)
    {
        var pending = _pendingContinuation;
        if (pending == null)
        {
            continuation = null!;
            return false;
        }
        _pendingContinuation = null;
        continuation = pending;
        return true;
    }

    /// <summary>
    /// 存储协程续延（由 ActorYieldAwaitable 在挂起点调用，运行在 Worker 线程）
    /// 包装异常处理：恢复段异常同样走 HandleError，不击穿 Worker
    /// </summary>
    internal void StoreContinuation(Action rawContinuation, Action onSuspend)
    {
        _pendingContinuation = () =>
        {
            try
            {
                rawContinuation();
            }
            catch (OperationCanceledException)
            {
                // Actor 销毁引发的取消，正常退出
            }
            catch (Exception ex)
            {
                HandleError(ex, null);
            }
        };
        onSuspend();
    }

    /// <summary>
    /// 尝试从邮箱取一条消息（由 Worker 调用，邮箱为单消费者）
    /// </summary>
    internal bool TryReadMessage(out IAMessage msg) => _reader.TryRead(out msg!);

    protected abstract ValueTask OnReceiveAsync(IAMessage msg);

    /// <summary>
    /// 分发一条消息（由 Worker 调用，同步段在 Worker 线程执行）
    /// 业务协程挂起时续延存入 _pendingContinuation，由 Worker 检测
    /// </summary>
    internal void DispatchMessage(IAMessage msg)
    {
        ValueTask task;
        try
        {
            task = DispatchMessageAsync(msg);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            HandleError(ex, msg);
            return;
        }

        if (HasPendingContinuation || task.IsCompleted) return;

        Log.Error("[Actor] {Path} awaits a non-engine awaitable; coroutine escaped scheduler", Context.Path);
    }

    private ValueTask DispatchMessageAsync(IAMessage msg)
    {
        switch (msg)
        {
            case SystemStopMessage:
                return StopAsync();
            case TimerTickMessage tick:
                return Context.ExecuteTimerAsync(tick.TimerId);
            default:
                return OnReceiveAsync(msg);
        }
    }

    private async ValueTask StopAsync()
    {
        await Context.DisposeAsync();
        _disposed = true;
    }

    /// <summary>
    /// 兼容模式：独立消息循环（当没有调度器时使用）
    /// </summary>
    internal void StartStandalone()
    {
        _ = ProcessMessagesStandaloneAsync();
    }

    private async Task ProcessMessagesStandaloneAsync()
    {
        await foreach (var msg in _reader.ReadAllAsync(Context.CancellationToken))
        {
            if (msg is ISystemMessage sysMsg && sysMsg is SystemStopMessage)
            {
                await Context.DisposeAsync();
                return;
            }

            // 定时器到期：在调度上下文中执行回调
            if (msg is TimerTickMessage tick)
            {
                await Context.ExecuteTimerAsync(tick.TimerId);
                continue;
            }

            try
            {
                await OnReceiveAsync(msg).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleError(ex, msg);
                if (_shouldStopDueToError) break;
            }
        }
    }

    protected virtual void HandleError(Exception ex, IAMessage? msg)
    {
        // 简化版：业务异常仅记录，系统异常标记停止
        if (ex is BusinessException || ex is OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[BusinessError] {ex.Message}");
            return;
        }

        // 系统错误：标记停止（通过系统消息优雅退出）
        System.Diagnostics.Debug.WriteLine($"[CriticalError] {Context.Path}: {ex.GetType().Name}");
        _shouldStopDueToError = true;
        _ = TellAsync(new SystemStopMessage { Reason = "CriticalError" });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _writer.TryComplete();
        // 断开挂起协程引用（状态机由 Task 基础设施持有，可被 GC）
        _pendingContinuation = null;
        await Context.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 系统停止消息（触发Actor优雅退出）
/// </summary>
public sealed class SystemStopMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public string Reason { get; set; } = string.Empty;
}
