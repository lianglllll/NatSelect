using NatSelect.Logger;
using Serilog;
using System.Threading.Channels;

namespace NatSelect.Core;

public abstract class Actor : IAsyncDisposable
{
    private readonly ChannelReader<IAMessage> _reader;
    private readonly ChannelWriter<IAMessage> _writer;
    private bool _shouldStopDueToError;
    protected ILogger Log { get; }

    protected ActorContext Context { get; }

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

        // 启动处理循环（由调度器管理）
        _ = ProcessMessagesAsync(); 
    }

    internal ValueTask TellAsync(IAMessage msg) =>
        _writer.WriteAsync(msg);

    protected abstract ValueTask OnReceiveAsync(IAMessage msg);

    private async Task ProcessMessagesAsync()
    {
        await foreach (var msg in _reader.ReadAllAsync(Context.CancellationToken))
        {
            // 收到停止指令，优雅退出
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
            // 业务错误：记录日志，不中断
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