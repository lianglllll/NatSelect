using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatSelect.Core;

/// <summary>
/// Actor引用（值类型，零堆分配，线程安全）
/// 设计原则：仅含路由必要信息，不可变
/// </summary>
public readonly struct ActorRef : IEquatable<ActorRef>
{
    public ulong Id { get; }
    public string Path { get; } // 格式: /world/room_101/player_1001

    /// <summary>
    /// 创建有效Actor引用（Id=0视为无效）
    /// </summary>
    public ActorRef(ulong id, string path)
    {
        if (id == 0) throw new ArgumentException("Actor ID must be non-zero", nameof(id));
        Path = path ?? throw new ArgumentNullException(nameof(path));
        Id = id;
    }

    /// <summary>
    /// 无效引用标识（用于Parent=null等场景）
    /// </summary>
    public static ActorRef Invalid => new(0, "/invalid");

    // 值类型相等比较（高性能）
    public bool Equals(ActorRef other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is ActorRef other && Equals(other);
    public override int GetHashCode() => Id.GetHashCode();
    public static bool operator ==(ActorRef left, ActorRef right) => left.Equals(right);
    public static bool operator !=(ActorRef left, ActorRef right) => !left.Equals(right);
}