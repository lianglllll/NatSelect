using System.Collections.Concurrent;
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

        var actor = (Actor)Activator.CreateInstance(typeof(T), ctorArgs)!;

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

    public ValueTask StopActorAsync(ActorRef actorRef)
    {
        if (_actors.TryRemove(actorRef.ActorId, out var actor))
            return actor.DisposeAsync();
        return ValueTask.CompletedTask;
    }

    public bool IsActorAlive(ActorRef actorRef) => _actors.ContainsKey(actorRef.ActorId);
    public bool RegisterService(string name, ActorRef actorRef) => _services.TryAdd(name, actorRef);
    public bool UnregisterService(string name) => _services.TryRemove(name, out _);
    public ActorRef? LookupService(string name) => _services.TryGetValue(name, out var r) ? r : null;
    public void Watch(ActorRef watcher, ActorRef target)
    {
        _watchersByTarget
            .GetOrAdd(target.ActorId, _ => new ConcurrentDictionary<ulong, bool>())
            .TryAdd(watcher.ActorId, true);
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
            // 返回所有 Actor（简化版，显示完整的树）
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
        var children = new List<ActorSnapshot>();
        foreach (var childRef in actor.Context.Children.Values)
        {
            if (_actors.TryGetValue(childRef.ActorId, out var childActor))
            {
                children.Add(BuildActorSnapshot(childActor));
            }
        }

        return new ActorSnapshot(
            Self: actor.Context.Self,
            Name: actor.Context.Name,
            Path: actor.Context.Path,
            State: actor.Context.State,
            MailboxSize: 0, // TODO: 可以从 Channel 获取
            Children: children
        );
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
        if (!_watchersByTarget.TryGetValue(target.ActorId, out var watchers)) return;

        foreach (var (watcherId, _) in watchers)
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

        _watchersByTarget.TryRemove(target.ActorId, out _);
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