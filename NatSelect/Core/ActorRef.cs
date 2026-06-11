namespace NatSelect.Core;

/// <summary>
/// 分布式全局 Actor 引用
/// 设计原则：
/// 1. 值类型 (struct)，零堆分配
/// 2. 不可变 (readonly)
/// 3. 包含 NodeId 以支持跨节点路由
/// </summary>
public readonly struct ActorRef : IEquatable<ActorRef>
{
    /// <summary>
    /// 节点 ID (集群唯一)
    /// </summary>
    public ulong NodeId { get; }

    /// <summary>
    /// Actor ID (节点内唯一)
    /// </summary>
    public ulong ActorId { get; }

    /// <summary>
    /// 构造函数
    /// </summary>
    public ActorRef(ulong nodeId, ulong actorId)
    {
        if (actorId == 0) throw new ArgumentException("Actor ID must be non-zero", nameof(actorId));

        NodeId = nodeId;
        ActorId = actorId;
    }

    /// <summary>
    /// 私有构造：仅用于创建 Invalid 哨兵值
    /// </summary>
    private ActorRef(bool _) { NodeId = 0; ActorId = 0; }

    /// <summary>
    /// 判断是否是本地 Actor
    /// </summary>
    public bool IsLocal(ulong currentLocalNodeId) => NodeId == currentLocalNodeId;

    /// <summary>
    /// 无效引用 (用于空值判断)
    /// </summary>
    public static readonly ActorRef Invalid = new(false);

    public bool IsValid => ActorId != 0;

    // 相等性比较 (同时比较 NodeId 和 ActorId)
    public bool Equals(ActorRef other) => NodeId == other.NodeId && ActorId == other.ActorId;

    public override bool Equals(object? obj) => obj is ActorRef other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(NodeId, ActorId);

    public static bool operator ==(ActorRef left, ActorRef right) => left.Equals(right);
    public static bool operator !=(ActorRef left, ActorRef right) => !left.Equals(right);

    public override string ToString() => $"[{NodeId}:{ActorId}]";
}