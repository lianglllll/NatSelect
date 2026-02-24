using System.Collections.Concurrent;

namespace NatSelect.Core;

/// <summary>
/// 单机版Actor系统（MVP，可立即运行测试）
/// </summary>
public sealed class LocalActorSystem : IActorSystem
{
    private readonly ConcurrentDictionary<ulong, Actor> _actors = new();
    private readonly ConcurrentDictionary<string, ActorRef> _services = new();
    private readonly ConcurrentDictionary<(ulong, ulong), bool> _watching = new();
    private ulong _nextActorId = 1;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ActorRef SpawnActor<T>(ActorContext parent, string name, params object[] args)
        where T : Actor
    {
        // 生成唯一ID（含简单节点标识：高32位=0表示本节点）
        var id = ((ulong)0 << 32) | (ulong)Interlocked.Increment(ref _nextActorId);
        var path = $"{parent.Path}/{name}";
        var selfRef = new ActorRef(id, path);

        // 创建Context和Actor
        var context = new ActorContext(this, null!, selfRef, parent.Self, path); // owner稍后设置
        var actor = (Actor)Activator.CreateInstance(typeof(T), context, args)!;
        context.GetType().GetField("_owner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(context, actor); // 修复owner引用（简化处理）

        // 注册到系统
        _actors.TryAdd(id, actor);
        return selfRef;
    }

    public ValueTask SendAsync(ActorRef target, IMessage message)
    {
        if (_actors.TryGetValue(target.Id, out var actor))
            return actor.SendAsync(message);

        // 目标不存在：通知监视者（简化版）
        NotifyWatchers(target);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopActorAsync(ActorRef actorRef)
    {
        if (_actors.TryRemove(actorRef.Id, out var actor))
            return actor.DisposeAsync();
        return ValueTask.CompletedTask;
    }

    public bool IsActorAlive(ActorRef actorRef) => _actors.ContainsKey(actorRef.Id);
    public bool RegisterService(string name, ActorRef actorRef) => _services.TryAdd(name, actorRef);
    public bool UnregisterService(string name) => _services.TryRemove(name, out _);
    public ActorRef? LookupService(string name) => _services.TryGetValue(name, out var r) ? r : null;
    public void Watch(ActorRef watcher, ActorRef target) => _watching.TryAdd((watcher.Id, target.Id), true);
    public void Unwatch(ActorRef watcher, ActorRef target) => _watching.TryRemove((watcher.Id, target.Id), out _);
    public int GetTotalActorCount() => _actors.Count;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void NotifyWatchers(ActorRef target)
    {
        // 简化：遍历监视关系，发送Terminated消息（实际需查watcher是否存在）
        foreach (var ((watcherId, _), _) in _watching)
        {
            if (_actors.TryGetValue(watcherId, out var watcherActor))
            {
                _ = watcherActor.SendAsync(new TerminatedMessage
                {
                    ActorRef = target,
                    Sender = 0
                });
            }
        }
    }
}

/// <summary>
/// Actor终止通知消息
/// </summary>
public sealed class TerminatedMessage : ISystemMessage
{
    public ulong Sender { get; set; }
    public ActorRef ActorRef { get; set; }
}