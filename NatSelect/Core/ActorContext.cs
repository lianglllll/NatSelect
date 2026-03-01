using System.Collections.Concurrent;

namespace NatSelect.Core;

public sealed class ActorContext : IAsyncDisposable
{
    private readonly IActorSystem m_system;
    private readonly Actor m_owner;
    private readonly CancellationTokenSource m_cts = new();
    private readonly ConcurrentDictionary<string, ActorRef> m_children = new();
    private readonly HashSet<ActorRef> m_watching = new();

    public ActorRef Self { get; }
    public ActorRef? Parent { get; }
    public IReadOnlyDictionary<string, ActorRef> Children => m_children;
    public CancellationToken CancellationToken => m_cts.Token;
    public string Path { get; }

    public ActorContext(IActorSystem system, Actor owner, ActorRef self, ActorRef? parent = null, string? path = null)
    {
        m_system = system ?? throw new ArgumentNullException(nameof(system));
        m_owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Self = self;
        Parent = parent;
        Path = path ?? $"/anonymous_{Self.ActorId}";
    }

    public ValueTask SendAsync(ActorRef target, IAMessage message)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        message.Sender = Self;
        return m_system.SendAsync(target, message);
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

    public ActorRef? LookupService(string serviceName) =>
        m_system.LookupService(serviceName);

    public async ValueTask DisposeAsync()
    {
        m_cts.Cancel();

        // 停止所有子Actor
        foreach (var child in m_children.Values.ToArray())
        {
            await m_system.StopActorAsync(child).ConfigureAwait(false);
        }
        m_children.Clear();

        // 清理监视关系
        foreach (var target in m_watching.ToArray())
            m_system.Unwatch(Self, target);
        m_watching.Clear();

        m_cts.Dispose();
    }
}