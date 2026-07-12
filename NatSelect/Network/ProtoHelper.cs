using Google.Protobuf;
using Google.Protobuf.Reflection;
using Serilog;
using NatSelect.Common;
using NatSelect.Core;
using System.Buffers.Binary;

namespace NatSelect.Network;

/// <summary>
/// Protobuf序列化与反序列化
/// </summary>
public class ProtoHelper : Singleton<ProtoHelper>
{
    //考虑到每次输送类型名太长了，所以imessage类型一个序号
    private static Dictionary<int, Type> m_sequence2type = new Dictionary<int, Type>();
    private static Dictionary<Type, int> m_type2sequence = new Dictionary<Type, int>();

    public new void Init()
    {

    }

    public void UnInit()
    {

    }

    /// <summary>
    /// 自动扫描程序集，注册所有带 [ProtoId] 特性的 IMessage 类型
    /// </summary>
    public void AutoRegisterAll()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var types = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IMessage).IsAssignableFrom(t));

        foreach (var type in types)
        {
            var attr = type.GetCustomAttributes(typeof(ProtoIdAttribute), false)
                .FirstOrDefault() as ProtoIdAttribute;
            if (attr == null) continue;

            int id = attr.Id;

            // 检查重复注册
            if (m_sequence2type.TryGetValue(id, out var existingType) && existingType != type)
            {
                Log.Warning("[ProtoHelper] Proto ID {Id} already registered for {Existing}, skipping {New}",
                    id, existingType.Name, type.Name);
                continue;
            }

            m_sequence2type[id] = type;
            m_type2sequence[type] = id;
            Log.Debug("[ProtoHelper] Registered {Type} with ID {Id}", type.Name, id);
        }

        Log.Information("[ProtoHelper] Auto-registered {Count} proto message types", m_sequence2type.Count);
    }

    public bool Register<T>(int id) where T : IMessage
    {
        Type type = typeof(T);
        m_sequence2type[id] = type;
        m_type2sequence[type] = id;
        return true;
    }

    public int Type2Seq(Type type)
    {
        if (m_type2sequence.ContainsKey(type))
        {
            return m_type2sequence[type];
        }
        else
        {
            Log.Error($"[ProtoHelper.Type2Seq]未找到对应的协议类型:{type.ToString()}");
            return -1;
        }
    }

    public Type Seq2Type(int code)
    {
        if (m_sequence2type.ContainsKey(code))
        {
            return m_sequence2type[code];
        }
        else
        {
            Log.Error($"[ProtoHelper.Seq2Type]未找到对应的协议id:{code}");
            return null;
        }
    }

    public IMessage ByteArrayParse2IMessage(ReadOnlyMemory<byte> data)
    {
        /*            ushort typeCode = _GetUShort(data, 0);
                    Type t = Seq2Type(typeCode);
                    if (t == null)
                    {
                        Log.Error($"[ProtoHelper.ParseFrom]解析失败，协议号:{typeCode}");
                        return null;
                    }
                    var desc = t.GetProperty("Descriptor").GetValue(t) as MessageDescriptor;
                    var msg = desc.Parser.ParseFrom(data, 2, data.Length - 2);
                    return msg;*/

        // 直接操作内存，无需复制
        ReadOnlySpan<byte> span = data.Span;
        ushort typeCode = BinaryPrimitives.ReadUInt16BigEndian(span);

        Type t = Seq2Type(typeCode);
        var desc = t.GetProperty("Descriptor").GetValue(t) as MessageDescriptor;

        // 使用Span解析
        return desc.Parser.ParseFrom(span.Slice(2));
    }
    
    public byte[] IMessageParse2ByteArray(IMessage message)
    {
        //获取imessage类型所对应的编号，网络传输我们只传输编号
        using (var ds = DataStream.Allocate())
        {
            int code = Type2Seq(message.GetType());
            if (code == -1)
            {
                return null;
            }
            ds.WriteInt(message.CalculateSize() + 2);           //长度字段
            ds.WriteUShort((ushort)code);                       //协议编号字段
            message.WriteTo(ds);                                //数据
            return ds.ToArray();
        }
    }

    private ushort _GetUShort(byte[] data, int offset)
    {
        if (BitConverter.IsLittleEndian)
            return (ushort)(data[offset] << 8 | data[offset + 1]);
        return (ushort)(data[offset + 1] << 8 | data[offset]);
    }

    /// <summary>
    /// 序列化 Actor 消息为纯 Protobuf 字节数组（不含长度头，用于 envelope payload）
    /// </summary>
    public byte[] Serialize(IAMessage message)
    {
        if (message is IMessage protoMsg)
            return protoMsg.ToByteArray();
        throw new InvalidOperationException($"Cannot serialize {message.GetType()}: does not implement IMessage");
    }

    /// <summary>
    /// 反序列化字节数据为 Actor 消息（从 envelope 的 payload_type 获取类型信息）
    /// </summary>
    public IAMessage? Deserialize(ByteString data, ulong payloadType)
    {
        int typeCode = (int)payloadType;
        var type = Seq2Type(typeCode);
        if (type == null)
        {
            Log.Error("[ProtoHelper.Deserialize] Unknown payload type: {TypeCode}", typeCode);
            return null;
        }

        var desc = type.GetProperty("Descriptor")?.GetValue(type) as MessageDescriptor;
        if (desc == null)
        {
            Log.Error("[ProtoHelper.Deserialize] Cannot get descriptor for type: {Type}", type.Name);
            return null;
        }

        var msg = desc.Parser.ParseFrom(data.Memory.Span);
        return msg as IAMessage;
    }
}