using System.Collections.Concurrent;

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

    public ActorRef SpawnActor<T>(ActorContext parent, string name, params object[] args)
        where T : Actor
    {
        // 生成唯一ID（含简单节点标识：高32位=0表示本节点）
        var id = ((ulong)0 << 32) | (ulong)Interlocked.Increment(ref _nextActorId);
        var path = $"{parent.Path}/{name}";
        var selfRef = new ActorRef(0, id);

        // 创建Context和Actor
        var context = new ActorContext(this, selfRef, parent.Self, path);
        var actor = (Actor)Activator.CreateInstance(typeof(T), context, args)!;

        // 注册到系统
        _actors.TryAdd(id, actor);
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
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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