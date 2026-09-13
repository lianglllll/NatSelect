using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using NatSelect.Config.Template;
using NatSelect.Network;
using Serilog;

namespace NatSelect.Core;

/// <summary>
/// 单机版Actor系统（MVP，可立即运行测试）
/// </summary>
public sealed class LocalActorSystem : IActorSystem
{
    private readonly ConcurrentDictionary<ulong, Actor> _actors = new();
    private readonly ConcurrentDictionary<string, ActorRef> _services = new();
    // 索引：target ActorId -> (watcher ActorId -> true)，方便快速查找谁在监视某个目标
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, bool>> _watchersByTarget = new();
    private ulong _nextActorId = 1;
    private readonly ActorSystemConfig _config;
    private readonly ActorScheduler _scheduler;

    // 构造函数缓存：按 (Actor类型, 参数类型签名) 复用，避免高频 Spawn 的反射开销
    private static readonly ConcurrentDictionary<string, ConstructorInfo> s_ctorCache = new();

    public ulong NodeId { get; }

    public LocalActorSystem(ulong nodeId, ActorSystemConfig config)
    {
        NodeId = nodeId;
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // 初始化调度器
        _scheduler = new ActorScheduler();
        _scheduler.Start(config.WorkerThreads);
    }

    public ActorRef SpawnActor<T>(ActorContext parent, string name, params object[] args)
        where T : Actor
    {
        // 生成 Actor ID（节点内唯一，简单递增）
        var actorId = (ulong)Interlocked.Increment(ref _nextActorId);
        var path = $"{parent.Path}/{name}";
        var selfRef = new ActorRef(NodeId, actorId);

        // 创建Context和Actor
        var context = new ActorContext(this, selfRef, parent.Self, path, name);

        // 构建构造函数参数：context + args
        var ctorArgs = new object[1 + args.Length];
        ctorArgs[0] = context;
        if (args.Length > 0)
            Array.Copy(args, 0, ctorArgs, 1, args.Length);

        var actor = (Actor)GetOrCreateConstructor(typeof(T), ctorArgs).Invoke(ctorArgs);

        // 注入调度器与协程归属（协程原语需要回写续延到 Actor）
        actor.Scheduler = _scheduler;
        context.Owner = actor;

        // 注册到系统
        _actors.TryAdd(actorId, actor);
        return selfRef;
    }

    public ValueTask SendAsync(ActorRef target, IAMessage message)
    {
        if (_actors.TryGetValue(target.ActorId, out var actor))
            return actor.TellAsync(message);

        // 目标不存在：通知监视者（简化版）
        NotifyWatchers(target);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopActorAsync(ActorRef actorRef)
    {
        if (_actors.TryRemove(actorRef.ActorId, out var actor))
        {
            await actor.DisposeAsync();
            // 通知监视者：监督语义要求父 Actor 收到 TerminatedMessage
            NotifyWatchers(actorRef);
        }
    }

    public void OnActorTerminated(ActorRef actorRef)
    {
        _actors.TryRemove(actorRef.ActorId, out _);
        NotifyWatchers(actorRef);
    }

    public bool IsActorAlive(ActorRef actorRef) => _actors.ContainsKey(actorRef.ActorId);
    public bool RegisterService(string name, ActorRef actorRef) => _services.TryAdd(name, actorRef);
    public bool UnregisterService(string name) => _services.TryRemove(name, out _);
    public ActorRef? LookupService(string name) => _services.TryGetValue(name, out var r) ? r : null;
    public void Watch(ActorRef watcher, ActorRef target)
    {
        // 目标已死：立即补发终止通知（对齐 Erlang monitor 语义），
        // 避免监视者注册后永远等不到 TerminatedMessage 且条目永久残留
        if (!_actors.ContainsKey(target.ActorId))
        {
            SendTerminated(watcher.ActorId, target);
            return;
        }

        var watchers = _watchersByTarget
            .GetOrAdd(target.ActorId, _ => new ConcurrentDictionary<ulong, bool>());
        watchers.TryAdd(watcher.ActorId, true);

        // 注册窗口内目标死亡：若自身条目未被 NotifyWatchers 的通知轮带走，补发一次，
        // 防止目标恰在注册瞬间死亡导致通知丢失（业务侧 TerminatedMessage 处理需幂等）
        if (!_actors.ContainsKey(target.ActorId) && watchers.TryRemove(watcher.ActorId, out _))
        {
            if (watchers.IsEmpty) _watchersByTarget.TryRemove(target.ActorId, out _);
            SendTerminated(watcher.ActorId, target);
        }
    }

    public void Unwatch(ActorRef watcher, ActorRef target)
    {
        if (_watchersByTarget.TryGetValue(target.ActorId, out var watchers))
        {
            watchers.TryRemove(watcher.ActorId, out _);
            if (watchers.IsEmpty) _watchersByTarget.TryRemove(target.ActorId, out _);
        }
    }
    public int GetTotalActorCount() => _actors.Count;

    #region 诊断接口

    /// <summary>
    /// 获取所有顶层 Actor 的快照树
    /// </summary>
    public IReadOnlyList<ActorSnapshot> GetActorTree()
    {
        var result = new List<ActorSnapshot>();
        foreach (var actor in _actors.Values)
        {
            // 仅从根 Actor 开始构建，避免子树重复出现在结果中
            var parent = actor.Context.Parent;
            if (parent.HasValue && _actors.ContainsKey(parent.Value.ActorId)) continue;
            result.Add(BuildActorSnapshot(actor));
        }
        return result;
    }

    /// <summary>
    /// 获取指定 Actor 的快照
    /// </summary>
    public ActorSnapshot? GetActorSnapshot(ActorRef actorRef)
    {
        if (_actors.TryGetValue(actorRef.ActorId, out var actor))
        {
            return BuildActorSnapshot(actor);
        }
        return null;
    }

    private ActorSnapshot BuildActorSnapshot(Actor actor)
    {
        // 显式栈迭代实现后序遍历，避免深层 Actor 树触发线程栈溢出
        var snapshots = new Dictionary<ulong, ActorSnapshot>();
        var stack = new Stack<Actor>();
        stack.Push(actor);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (snapshots.ContainsKey(current.Context.Self.ActorId)) continue; // 重检时可能重复入栈

            var children = new List<ActorSnapshot>();
            bool allChildrenReady = true;
            foreach (var childRef in current.Context.Children.Values)
            {
                if (!_actors.TryGetValue(childRef.ActorId, out var childActor)) continue;

                if (snapshots.TryGetValue(childRef.ActorId, out var childSnapshot))
                {
                    children.Add(childSnapshot);
                }
                else
                {
                    allChildrenReady = false;
                    stack.Push(childActor);
                }
            }

            if (!allChildrenReady)
            {
                stack.Push(current); // 待子节点快照就绪后再重建
                continue;
            }

            snapshots[current.Context.Self.ActorId] = new ActorSnapshot(
                Self: current.Context.Self,
                Name: current.Context.Name,
                Path: current.Context.Path,
                State: current.Context.State,
                MailboxSize: current.MailboxSize,
                MailboxCapacity: current.MailboxCapacity,
                DroppedMessageCount: current.DroppedMessageCount,
                MergedTickCount: current.MergedTickCount,
                Children: children
            );
        }

        return snapshots[actor.Context.Self.ActorId];
    }

    #endregion

    /// <summary>
    /// 发送远程消息（通过 NetworkService 路由）
    /// </summary>
    public async ValueTask SendRemoteAsync(ActorRef sender, ActorRef target, IAMessage message)
    {
        if (_networkService == null)
        {
            Log.Warning("[ActorSystem] Cannot send remote message: NetworkService not connected. Target: {Target}", target);
            return;
        }
        await _networkService.SendRemoteAsync(sender, target, message);
    }

    /// <summary>
    /// 注入 NetworkService（引擎启动后调用）
    /// </summary>
    public void SetNetworkService(NetworkService networkService)
    {
        _networkService = networkService;
    }

    private NetworkService? _networkService;

    public async ValueTask DisposeAsync()
    {
        // 停止调度器
        await _scheduler.StopAsync();

        // 停止所有 Actor
        foreach (var actor in _actors.Values)
        {
            await actor.DisposeAsync();
        }
        _actors.Clear();

        Log.Information("[ActorSystem] Disposed");
    }

    private void NotifyWatchers(ActorRef target)
    {
        // 先移除条目再通知：并发注册的监视者会重建条目并自行重检补发，
        // 避免枚举期间新注册的 watcher 被无通知移除（监督链闭环的关键）
        if (!_watchersByTarget.TryRemove(target.ActorId, out var watchers)) return;

        foreach (var (watcherId, _) in watchers)
        {
            SendTerminated(watcherId, target);
        }
    }

    private void SendTerminated(ulong watcherId, ActorRef target)
    {
        if (_actors.TryGetValue(watcherId, out var watcherActor))
        {
            _ = watcherActor.TellAsync(new TerminatedMessage
            {
                ActorRef = target,
                Sender = ActorRef.Invalid
            });
        }
    }

    private static ConstructorInfo GetOrCreateConstructor(Type actorType, object[] ctorArgs)
    {
        var paramTypes = new Type[ctorArgs.Length];
        for (int i = 0; i < ctorArgs.Length; i++)
            paramTypes[i] = ctorArgs[i]?.GetType() ?? typeof(object);

        var key = BuildCtorCacheKey(actorType, paramTypes);
        return s_ctorCache.GetOrAdd(key, _ => FindConstructor(actorType, paramTypes));
    }

    private static string BuildCtorCacheKey(Type actorType, Type[] paramTypes)
    {
        var sb = new StringBuilder(actorType.FullName);
        foreach (var t in paramTypes)
            sb.Append('|').Append(t.FullName);
        return sb.ToString();
    }

    private static ConstructorInfo FindConstructor(Type actorType, Type[] paramTypes)
    {
        foreach (var ctor in actorType.GetConstructors())
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length != paramTypes.Length) continue;

            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                // 按可赋值性匹配（兼容派生类实参），与 Activator.CreateInstance 语义一致
                if (!parameters[i].ParameterType.IsAssignableFrom(paramTypes[i]))
                {
                    matches = false;
                    break;
                }
            }
            if (matches) return ctor;
        }

        throw new InvalidOperationException(
            $"Actor type {actorType.Name} has no constructor matching the given argument types");
    }
}

/// <summary>
/// Actor终止通知消息
/// </summary>
public sealed class TerminatedMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public ActorRef ActorRef { get; set; }
}