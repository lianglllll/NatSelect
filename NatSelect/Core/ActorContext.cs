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
    /// </summary>
    /// <param name="intervalMs">间隔毫秒</param>
    /// <param name="callback">回调</param>
    /// <param name="repeat">是否循环</param>
    /// <returns>定时器ID</returns>
    public int SetTimer(int intervalMs, Func<ValueTask> callback, bool repeat = false)
    {
        var id = Interlocked.Increment(ref m_nextTimerId);
        var timer = new ActorTimer(id, intervalMs, repeat, callback);
        m_timers.TryAdd(id, timer);
        return id;
    }

    /// <summary>
    /// 取消定时器
    /// </summary>
    public async ValueTask CancelTimer(int timerId)
    {
        if (m_timers.TryRemove(timerId, out var timer))
        {
            await timer.DisposeAsync();
        }
    }

    /// <summary>
    /// 一次性延迟执行
    /// </summary>
    public int ScheduleOnce(int delayMs, Func<ValueTask> callback)
    {
        return SetTimer(delayMs, callback, repeat: false);
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