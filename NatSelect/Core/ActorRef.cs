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
        // NodeId 可以为 0 吗？通常本地节点启动时会分配一个非零 NodeId
        // 如果允许 0 代表“当前节点”，则逻辑需特殊处理。建议 NodeId 也全局唯一且非零。

        NodeId = nodeId;
        ActorId = actorId;
    }

    /// <summary>
    /// 判断是否是本地 Actor
    /// </summary>
    public bool IsLocal(ulong currentLocalNodeId) => NodeId == currentLocalNodeId;

    /// <summary>
    /// 无效引用 (用于空值判断)
    /// </summary>
    public static ActorRef Invalid => new(0, 0);

    public bool IsValid => ActorId != 0;

    // 相等性比较 (同时比较 NodeId 和 ActorId)
    public bool Equals(ActorRef other) => NodeId == other.NodeId && ActorId == other.ActorId;

    public override bool Equals(object? obj) => obj is ActorRef other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(NodeId, ActorId);

    public static bool operator ==(ActorRef left, ActorRef right) => left.Equals(right);
    public static bool operator !=(ActorRef left, ActorRef right) => !left.Equals(right);

    public override string ToString() => $"[{NodeId}:{ActorId}]";
}