using System.Threading.Channels;
using Serilog;

namespace NatSelect.Core;

/// <summary>
/// Actor 调度器
/// 采用类似 Skynet 的调度模型：
/// - Actor 有消息时放入就绪队列
/// - Worker 线程从队列取出 Actor 处理消息
/// - 每处理 N 条消息后让出执行权，保证公平调度
/// </summary>
public sealed class ActorScheduler : IAsyncDisposable
{
    private readonly Channel<Actor> _readyQueue;
    private Task[] _workers = Array.Empty<Task>();
    private CancellationTokenSource _cts = new();
    private bool _isStopping;

    // 每次调度处理的最大消息数（防止单个 Actor 独占 Worker）
    private const int MaxMessagesPerSlice = 10;

    public ActorScheduler()
    {
        _readyQueue = Channel.CreateUnbounded<Actor>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 启动调度器
    /// </summary>
    /// <param name="workerCount">Worker 线程数</param>
    public void Start(int workerCount)
    {
        _workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            int workerId = i;
            _workers[i] = Task.Run(() => WorkerLoopAsync(workerId));
        }

        Log.Information("[ActorScheduler] Started with {Count} workers", workerCount);
    }

    private async Task WorkerLoopAsync(int workerId)
    {
        var reader = _readyQueue.Reader;

        while (!_isStopping)
        {
            try
            {
                var actor = await reader.ReadAsync(_cts.Token);

                if (actor.IsDisposed) continue;

                // 处理最多 N 条消息，然后让出
                int processed = 0;
                while (processed < MaxMessagesPerSlice && !actor.IsDisposed)
                {
                    bool hasMore = await actor.ProcessOneMessageAsync();
                    processed++;

                    if (!hasMore) break;
                }

                // 如果还有消息，重新放入队列
                if (!actor.IsDisposed && actor.HasPendingMessages)
                {
                    Schedule(actor);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ActorScheduler] Worker {Id} error", workerId);
            }
        }

        Log.Debug("[ActorScheduler] Worker {Id} stopped", workerId);
    }

    /// <summary>
    /// 将 Actor 放入就绪队列（线程安全）
    /// </summary>
    public void Schedule(Actor actor)
    {
        if (_isStopping || actor.IsDisposed) return;

        // 使用原子操作防止重复入队
        if (actor.TryMarkScheduled())
        {
            _readyQueue.Writer.TryWrite(actor);
        }
    }

    /// <summary>
    /// 停止调度器
    /// </summary>
    public async Task StopAsync()
    {
        _isStopping = true;
        _cts.Cancel();
        _readyQueue.Writer.TryComplete();

        // 等待所有 Worker 退出
        if (_workers.Length > 0)
        {
            await Task.WhenAll(_workers);
        }

        Log.Information("[ActorScheduler] Stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
