using System.Collections.Concurrent;

namespace NatSelect.Core;

public sealed class ActorContext : IAsyncDisposable
{
    private readonly IActorSystem m_system;
    private readonly CancellationTokenSource m_cts = new();
    private readonly ConcurrentDictionary<string, ActorRef> m_children = new();
    private readonly HashSet<ActorRef> m_watching = new();
    private readonly ConcurrentDictionary<int, ActorTimer> m_timers = new();
    private int m_nextTimerId = 1;
    private ActorState m_state = ActorState.Created;

    public ActorRef Self { get; }
    public ActorRef? Parent { get; }
    public string Name { get; }
    public IReadOnlyDictionary<string, ActorRef> Children => m_children;
    public CancellationToken CancellationToken => m_cts.Token;
    public string Path { get; }
    public ActorState State => m_state;

    /// <summary>
    /// 所属 Actor（由 ActorSystem 在 Spawn 时注入，供协程原语回写续延）
    /// </summary>
    internal Actor? Owner { get; set; }

    public ActorContext(IActorSystem system, ActorRef self, ActorRef? parent = null, string? path = null, string? name = null)
    {
        m_system = system ?? throw new ArgumentNullException(nameof(system));
        Self = self;
        Parent = parent;
        Name = name ?? $"anonymous_{Self.ActorId}";
        Path = path ?? $"/{Name}";
        m_state = ActorState.Running;
    }

    public ValueTask SendAsync(ActorRef target, IAMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        message.Sender = Self;

        // 根据目标节点判断是本地消息还是远程消息
        if (target.IsLocal(m_system.NodeId))
            return m_system.SendAsync(target, message);
        else
            return m_system.SendRemoteAsync(Self, target, message);
    }

    public ActorRef SpawnChild<T>(string name, params object[] args) where T : Actor
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Child name required", nameof(name));
        if (m_children.ContainsKey(name))
            throw new InvalidOperationException($"Child '{name}' already exists in {Path}");

        var childRef = m_system.SpawnActor<T>(this, name, args);
        m_children.TryAdd(name, childRef);
        m_system.Watch(Self, childRef);
        m_watching.Add(childRef);
        return childRef;
    }

    public async ValueTask StopChildAsync(string name)
    {
        if (m_children.TryRemove(name, out var childRef))
        {
            m_watching.Remove(childRef);
            await m_system.StopActorAsync(childRef).ConfigureAwait(false);
        }
    }

    public void Watch(ActorRef target)
    {
        m_system.Watch(Self, target);
        lock (m_watching) m_watching.Add(target);
    }

    #region 定时器

    /// <summary>
    /// 创建定时器
    /// 到期时向自身邮箱投递 TimerTickMessage，回调由调度器串行执行
    /// </summary>
    /// <param name="intervalMs">间隔毫秒</param>
    /// <param name="callback">回调（在 Actor 调度上下文中执行，与消息处理串行）</param>
    /// <param name="repeat">是否循环</param>
    /// <returns>定时器ID</returns>
    public int SetTimer(int intervalMs, Func<ValueTask> callback, bool repeat = false)
    {
        var id = Interlocked.Increment(ref m_nextTimerId);
        var timer = new ActorTimer(id, intervalMs, repeat, callback, m_system, Self);
        m_timers.TryAdd(id, timer);
        return id;
    }

    /// <summary>
    /// 取消定时器（已投递但未执行的到期消息会被自动跳过）
    /// </summary>
    public async ValueTask CancelTimer(int timerId)
    {
        if (m_timers.TryRemove(timerId, out var timer))
        {
            await timer.DisposeAsync();
        }
    }

    /// <summary>
    /// 一次性延迟执行（回调在 Actor 调度上下文中执行）
    /// </summary>
    public int ScheduleOnce(int delayMs, Func<ValueTask> callback)
    {
        return SetTimer(delayMs, callback, repeat: false);
    }

    /// <summary>
    /// 执行定时器到期回调（由 Actor 基类在调度上下文中调用）
    /// 一次性定时器执行后自动移除并释放
    /// </summary>
    internal async ValueTask ExecuteTimerAsync(int timerId)
    {
        if (!m_timers.TryGetValue(timerId, out var timer)) return; // 已被取消

        try
        {
            await timer.ExecuteCallbackAsync();
        }
        finally
        {
            if (!timer.IsRepeating)
            {
                m_timers.TryRemove(timerId, out _);
                await timer.DisposeAsync();
            }
        }
    }

    #endregion

    #region 协程原语

    /// <summary>
    /// 显式让出执行权：挂起协程并立即重新入队，下轮由 Worker 恢复
    /// </summary>
    public ActorYieldAwaitable YieldAsync()
    {
        var owner = Owner ?? throw new InvalidOperationException("YieldAsync requires an owning actor");
        return new ActorYieldAwaitable(owner, () => owner.Scheduler?.Schedule(owner));
    }

    /// <summary>
    /// 延迟指定毫秒后恢复（挂起期间不占 Worker）
    /// </summary>
    public ActorYieldAwaitable DelayAsync(int delayMs)
    {
        if (delayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(delayMs), "delayMs must be non-negative");

        var owner = Owner ?? throw new InvalidOperationException("DelayAsync requires an owning actor");
        return new ActorYieldAwaitable(owner, () =>
        {
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                timer!.Dispose();
                owner.Scheduler?.Schedule(owner);
            }, null, delayMs, Timeout.Infinite);
        });
    }

    #endregion

    public ActorRef? LookupService(string serviceName) =>
        m_system.LookupService(serviceName);

    public async ValueTask DisposeAsync()
    {
        m_state = ActorState.Stopping;
        m_cts.Cancel();

        // 1. 取消所有定时器
        foreach (var timer in m_timers.Values)
        {
            await timer.DisposeAsync();
        }
        m_timers.Clear();

        // 2. 停止所有子Actor
        foreach (var child in m_children.Values.ToArray())
        {
            await m_system.StopActorAsync(child).ConfigureAwait(false);
        }
        m_children.Clear();

        // 3. 清理监视关系
        foreach (var target in m_watching.ToArray())
            m_system.Unwatch(Self, target);
        m_watching.Clear();

        m_state = ActorState.Stopped;
        m_cts.Dispose();
    }
}