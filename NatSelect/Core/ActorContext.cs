using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatSelect.Core;


/// <summary>
/// Actor运行时上下文（每个Actor独占，线程安全）
/// </summary>
public sealed class ActorContext : IAsyncDisposable
{
    private readonly IActorSystem _system;
    private readonly Actor _owner;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, ActorRef> _children = new();
    private readonly HashSet<ActorRef> _watching = new();

    public ActorRef Self { get; }
    public ActorRef? Parent { get; }
    public IReadOnlyDictionary<string, ActorRef> Children => _children;
    public CancellationToken CancellationToken => _cts.Token;
    public string Path { get; }

    /// <summary>
    /// 构造函数（Self必须为有效引用，Parent可为空）
    /// </summary>
    internal ActorContext(
        IActorSystem system,
        Actor owner,
        ActorRef self, // ✅ 非空struct（编译期保障）
        ActorRef? parent = null,
        string? path = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Self = self; // 直接赋值（已验证非空）
        Parent = parent;
        Path = path ?? $"/anonymous_{Self.Id}";
    }

    /// <summary>
    /// 发送消息（自动注入Sender）
    /// </summary>
    public ValueTask TellAsync(ActorRef target, IMessage message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        message.Sender = Self.Id; // 关键：自动标记发送者
        return _system.SendAsync(target, message);
    }

    /// <summary>
    /// 创建子Actor（自动注册父子关系）
    /// </summary>
    public ActorRef SpawnChild<T>(string name, params object[] args) where T : Actor
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Child name required", nameof(name));
        if (_children.ContainsKey(name))
            throw new InvalidOperationException($"Child '{name}' already exists in {Path}");

        var childRef = _system.SpawnActor<T>(this, name, args);
        _children.TryAdd(name, childRef);
        _system.Watch(Self, childRef); // 自动监视子Actor
        _watching.Add(childRef);
        return childRef;
    }

    /// <summary>
    /// 停止子Actor（级联清理）
    /// </summary>
    public async ValueTask StopChildAsync(string name)
    {
        if (_children.TryRemove(name, out var childRef))
        {
            _watching.Remove(childRef);
            await _system.StopActorAsync(childRef).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 监视其他Actor（接收Terminated消息）
    /// </summary>
    public void Watch(ActorRef target)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        _system.Watch(Self, target);
        lock (_watching) _watching.Add(target);
    }

    /// <summary>
    /// 查找全局服务（如"gateway_service"）
    /// </summary>
    public ActorRef? LookupService(string serviceName) =>
        _system.LookupService(serviceName);

    // ===== 资源清理 =====
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        // 停止所有子Actor（游戏场景：房间销毁清理玩家）
        foreach (var child in _children.Values.ToArray())
        {
            await _system.StopActorAsync(child).ConfigureAwait(false);
        }
        _children.Clear();

        // 清理监视关系
        foreach (var target in _watching.ToArray())
            _system.Unwatch(Self, target);
        _watching.Clear();

        _cts.Dispose();
    }
}