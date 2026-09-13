using NatSelect.Common;
using NatSelect.Logger;
using Serilog;
using System.Threading.Channels;

namespace NatSelect.Core;

public abstract class Actor : IAsyncDisposable
{
    private readonly ChannelReader<IAMessage> _reader;
    private readonly ChannelWriter<IAMessage> _writer;
    private readonly int _mailboxCapacity;
    private volatile bool _disposed;
    private int _scheduled = 0; // 0 = not scheduled, 1 = scheduled (防止重复入队)
    private int _processing = 0; // 0 = 空闲, 1 = 执行中（防止并发执行流）
    private volatile Action? _pendingContinuation; // 挂起协程的续延（同一 Actor 至多一个）
    private int _droppedMessageCount;
    private int _mergedTickCount;

    // 调度上下文标记：引擎 awaitable 只能在 Worker 调度该 Actor 期间被 await，
    // 逃逸到线程池的协程再次 await 引擎原语时会立即失败
    private static readonly AsyncLocal<Actor?> s_dispatchContext = new();

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
    /// 邮箱当前积压消息数
    /// </summary>
    public int MailboxSize => _reader.Count;

    /// <summary>
    /// 邮箱容量
    /// </summary>
    public int MailboxCapacity => _mailboxCapacity;

    /// <summary>
    /// 因邮箱满被丢弃的消息总数
    /// </summary>
    public int DroppedMessageCount => Volatile.Read(ref _droppedMessageCount);

    /// <summary>
    /// 被合并的定时器到期消息总数
    /// </summary>
    public int MergedTickCount => Volatile.Read(ref _mergedTickCount);

    /// <summary>
    /// 邮箱中是否还有待处理消息
    /// </summary>
    public bool HasPendingMessages => _reader.TryPeek(out _);

    protected Actor(ActorContext context, int mailboxCapacity = 1024)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _mailboxCapacity = mailboxCapacity;
        var channel = Channel.CreateBounded<IAMessage>(
            new BoundedChannelOptions(mailboxCapacity)
            {
                // Wait 模式下 TryWrite 满时返回 false；丢弃决策由 TellAsync 显式处理。
                // DropWrite 的 TryWrite 永远返回 true（静默丢弃），无法感知丢失。
                FullMode = BoundedChannelFullMode.Wait
            });
        _reader = channel.Reader;
        _writer = channel.Writer;

        Log = NSLogger.ForContext(context);

        // 注意：不再自动启动 ProcessMessagesAsync，由调度器管理
    }

    /// <summary>
    /// 投递消息到邮箱，并通知调度器
    /// </summary>
    internal ValueTask TellAsync(IAMessage msg)
    {
        if (_disposed) return ValueTask.CompletedTask;

        if (!_writer.TryWrite(msg))
        {
            if (msg is ISystemMessage)
            {
                // 系统消息必须送达：邮箱满时驱逐最旧消息腾位（停止/终止信号丢失会破坏监督链）。
                // Worker 可能并发消费，驱逐失败时立即重试写入。
                while (!_writer.TryWrite(msg))
                {
                    if (_disposed) return ValueTask.CompletedTask;
                    if (!_reader.TryRead(out var evicted))
                        continue; // Worker 已消费出空间，直接重试写入

                    var evictedTotal = Interlocked.Increment(ref _droppedMessageCount);
                    Log.Warning("[Actor] {Path} mailbox full ({Capacity}), evicted {EvictedType} to deliver system message {Type} (total evicted: {Total})",
                        Context.Path, _mailboxCapacity, evicted.GetType().Name, msg.GetType().Name, evictedTotal);
                }
            }
            else
            {
                // 普通消息邮箱满时丢弃（防雪崩），必须记录让上层可感知
                var dropped = Interlocked.Increment(ref _droppedMessageCount);
                Log.Warning("[Actor] {Path} mailbox full ({Capacity}), dropped {Type} (total: {Dropped})",
                    Context.Path, _mailboxCapacity, msg.GetType().Name, dropped);
                return ValueTask.CompletedTask;
            }
        }

        // 协程挂起中不入队：恢复后由 Worker 收尾兑底重新入队
        if (!HasPendingContinuation)
            Scheduler?.Schedule(this);

        return ValueTask.CompletedTask;
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
        // 引擎 awaitable 必须在调度上下文内被 await：逃逸到线程池的协程在此立即失败
        if (!ReferenceEquals(s_dispatchContext.Value, this))
        {
            throw new InvalidOperationException(
                $"[Actor] {Context.Path} awaits engine awaitable outside dispatch context; coroutine escaped scheduler");
        }

        _pendingContinuation = () =>
        {
            try
            {
                s_dispatchContext.Value = this;
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
            finally
            {
                s_dispatchContext.Value = null;
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
            s_dispatchContext.Value = this;
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
        finally
        {
            s_dispatchContext.Value = null;
        }

        if (HasPendingContinuation || task.IsCompleted) return;

        // 业务代码 await 了原生异步方法：协程已脱离调度器，串行保证被破坏
        Log.Error("[Actor] {Path} awaits a non-engine awaitable; coroutine escaped scheduler", Context.Path);
    }

    private ValueTask DispatchMessageAsync(IAMessage msg)
    {
        switch (msg)
        {
            case SystemStopMessage:
                return StopAsync();
            case TimerTickMessage tick:
                DrainDuplicateTicks(tick.TimerId);
                return Context.ExecuteTimerAsync(tick.TimerId);
            case TerminatedMessage terminated:
                // 子 Actor 终止：先清理上下文中的引用，业务层仍可收到该消息做重启等决策
                Context.RemoveChild(terminated.ActorRef);
                return OnReceiveAsync(msg);
            default:
                return OnReceiveAsync(msg);
        }
    }

    /// <summary>
    /// 合并积压的同一定时器到期消息：慢 Actor 下 tick 会在邮箱中堆积，
    /// 丢弃连续的重复 tick 只保留一个，避免突发追赶执行
    /// </summary>
    private void DrainDuplicateTicks(int timerId)
    {
        while (_reader.TryPeek(out var next) && next is TimerTickMessage dup && dup.TimerId == timerId)
        {
            _reader.TryRead(out _);
            Interlocked.Increment(ref _mergedTickCount);
        }
    }

    private async ValueTask StopAsync()
    {
        if (_disposed) return;
        await Context.DisposeAsync();
        _disposed = true;
        // 从系统摘除并通知监视者（监督链路的关键一环）
        Context.NotifyTerminated();
    }

    protected virtual void HandleError(Exception ex, IAMessage? msg)
    {
        // 业务异常仅记录，系统错误才停止 Actor（由 StopAsync 摘除注册并通知监视者）
        if (ex is BusinessException || ex is OperationCanceledException)
        {
            Log.Warning("[Actor] {Path} business error: {Message}", Context.Path, ex.Message);
            return;
        }

        Log.Error(ex, "[Actor] {Path} critical error while handling {MessageType}",
            Context.Path, msg?.GetType().Name ?? "unknown");
        Context.SetErrorState();
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
