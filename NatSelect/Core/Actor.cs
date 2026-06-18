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

    protected ILogger Log { get; }

    /// <summary>
    /// Actor 上下文（internal 供引擎内部访问）
    /// </summary>
    internal ActorContext Context { get; }

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

        // 通知调度器此 Actor 有新消息
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

    protected abstract ValueTask OnReceiveAsync(IAMessage msg);

    /// <summary>
    /// 处理一条消息（由调度器调用）
    /// </summary>
    /// <returns>true = 还有更多消息, false = 邮箱已空或已停止</returns>
    internal async ValueTask<bool> ProcessOneMessageAsync()
    {
        if (_disposed || _shouldStopDueToError)
        {
            ClearScheduled();
            return false;
        }

        if (!await _reader.WaitToReadAsync())
        {
            ClearScheduled();
            return false;
        }

        if (!_reader.TryRead(out var msg))
        {
            ClearScheduled();
            return false;
        }

        // 收到停止指令，优雅退出
        if (msg is ISystemMessage sysMsg && sysMsg is SystemStopMessage)
        {
            await Context.DisposeAsync();
            _disposed = true;
            ClearScheduled();
            return false;
        }

        try
        {
            await OnReceiveAsync(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            HandleError(ex, msg);
            if (_shouldStopDueToError)
            {
                ClearScheduled();
                return false;
            }
        }

        ClearScheduled();

        // 返回是否还有消息
        return _reader.TryPeek(out _);
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

    protected virtual void HandleError(Exception ex, IAMessage msg)
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
