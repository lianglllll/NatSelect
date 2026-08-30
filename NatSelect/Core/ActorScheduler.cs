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

                ProcessActor(actor);
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
    /// 推进单个 Actor：优先恢复挂起协程，否则消费邮箱消息
    /// </summary>
    private void ProcessActor(Actor actor)
    {
        actor.ClearScheduled();

        // 其他 Worker 正在执行该 Actor：放回稍后重试
        if (!actor.TryBeginProcessing())
        {
            Schedule(actor);
            return;
        }

        // 推进预算：协程恢复一次即交还，同步消息可批量处理
        int budget = MaxMessagesPerSlice;
        while (budget-- > 0 && !actor.IsDisposed)
        {
            // 1. 优先推进挂起协程（恢复条件已满足才会在队列中）
            if (actor.TryTakeContinuation(out var continuation))
            {
                continuation();
                break;
            }

            // 2. 无挂起协程：消费邮箱消息（同步段在 Worker 线程执行）
            if (!actor.TryReadMessage(out var msg)) break;

            actor.DispatchMessage(msg);
            if (actor.HasPendingContinuation) break;
        }

        actor.EndProcessing();

        // 挂起中的 Actor 不重新入队（由挂起动作的恢复机制负责触发）
        if (!actor.IsDisposed && !actor.HasPendingContinuation && actor.HasPendingMessages)
        {
            Schedule(actor);
        }
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
