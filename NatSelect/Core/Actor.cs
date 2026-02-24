using System.Threading.Channels;

namespace NatSelect.Core;


/// <summary>
/// Actor抽象基类（单线程消息处理，保序保安全）
/// </summary>
public abstract class Actor : IAsyncDisposable
{
    private readonly ChannelReader<IMessage> _reader;
    private readonly ChannelWriter<IMessage> _writer;
    private bool _shouldStopDueToError;

    protected ActorContext Context { get; }

    protected Actor(ActorContext context, int mailboxCapacity = 1024)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        var channel = Channel.CreateBounded<IMessage>(
            new BoundedChannelOptions(mailboxCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite // 邮箱满时丢弃新消息（防雪崩）
            });
        _reader = channel.Reader;
        _writer = channel.Writer;
        _ = ProcessMessagesAsync(); // 启动处理循环（由调度器管理）
    }

    /// <summary>
    /// 外部发送入口（线程安全）
    /// </summary>
    internal ValueTask SendAsync(IMessage msg) =>
        _writer.WriteAsync(msg);

    /// <summary>
    /// 子类重写：顺序处理消息（无并发风险）
    /// </summary>
    protected abstract ValueTask OnReceiveAsync(IMessage msg);

    /// <summary>
    /// 消息处理循环（单线程顺序执行）
    /// </summary>
    private async Task ProcessMessagesAsync()
    {
        await foreach (var msg in _reader.ReadAllAsync(Context.CancellationToken))
        {
            if (msg is ISystemMessage sysMsg && sysMsg is SystemStopMessage)
            {
                await Context.DisposeAsync();
                return; // 收到停止指令，优雅退出
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

    /// <summary>
    /// 错误处理（分级策略+防雪崩）
    /// </summary>
    protected virtual void HandleError(Exception ex, IMessage msg)
    {
        // 简化版：业务异常仅记录，系统异常标记停止
        if (ex is BusinessException || ex is OperationCanceledException)
        {
            // 业务错误：记录日志，不中断
            System.Diagnostics.Debug.WriteLine($"[BusinessError] {ex.Message}");
            return;
        }

        // 系统错误：标记停止（通过系统消息优雅退出）
        System.Diagnostics.Debug.WriteLine($"[CriticalError] {Context.Path}: {ex.GetType().Name}");
        _shouldStopDueToError = true;
        _ = SendAsync(new SystemStopMessage { Reason = "CriticalError" });
    }

    public async ValueTask DisposeAsync()
    {
        _writer.TryComplete();
        await Context.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 系统停止消息（触发Actor优雅退出）
/// </summary>
public sealed class SystemStopMessage : ISystemMessage
{
    public ulong Sender { get; set; }
    public string Reason { get; set; } = string.Empty;
}