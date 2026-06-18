namespace NatSelect.Network;

/// <summary>
/// 标注 Protobuf 消息类型的协议编号，用于网络序列化/反序列化时自动注册
/// 使用方式：在生成的 Protobuf 消息 partial 类上添加此特性，或通过扩展部分类标注
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ProtoIdAttribute : Attribute
{
    public int Id { get; }

    public ProtoIdAttribute(int id)
    {
        if (id <= 0)
            throw new ArgumentException("Proto ID must be positive", nameof(id));
        Id = id;
    }
}
