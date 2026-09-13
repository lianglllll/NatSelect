using Google.Protobuf;
using NatSelect.Core;
using Serilog;
using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NatSelect.Network;

public class TcpConnection
{
    // Socket Handle 值在关闭后会被 OS 复用，不能作为连接标识，改用进程内原子计数器
    private static long s_nextConnectionId;

    private readonly Socket _socket;
    private readonly LengthFieldDecoder _decoder;

    private readonly Channel<byte[]> _sendQueue;
    private volatile bool _isClosed;
    // 断线回调只允许触发一次（主动/被动断线路径可能并发汇合到 Close）
    private int _disconnectNotified;

    private readonly Action<long, IMessage> _onMessageReceived;
    private readonly Action<long> _onDisconnected;

    /// <summary>
    /// 连接唯一标识（进程内递增，永不复用）
    /// </summary>
    public long ConnectionId { get; }

    public TcpConnection(
        Socket socket,
        Action<long, IMessage> onMessageReceived,
        Action<long> onDisconnected)
    {
        _socket = socket;
        _onMessageReceived = onMessageReceived;
        _onDisconnected = onDisconnected;

        ConnectionId = Interlocked.Increment(ref s_nextConnectionId);

        // 有界发送队列：远端消费慢时直接断开连接，避免内存无限积压。
        // Wait 模式下 TryWrite 满时返回 false；DropWrite 的 TryWrite 恒返回 true（静默丢弃），满检测会失效。
        _sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        // 初始化解码器
        _decoder = new LengthFieldDecoder(
            socket,
            64 * 1024,
            0, 4, 0, 4,
            OnDataReceivedInternal,
            OnDisconnectedInternal
        );
    }

    public void Start()
    {
        _ = _decoder.StartAsync();
        _ = ProcessSendQueueAsync();
        Log.Debug("Connection {Id} started (No Encryption)", ConnectionId);
    }

    private void OnDataReceivedInternal(ReadOnlyMemory<byte> data)
    {
        if (_isClosed) return;

        try
        {
            // [删除] 解密步骤
            // var decrypted = _encryption.Decrypt(data);

            // 直接使用原始数据进行反序列化
            // 假设 data 已经是纯净的 Protobuf 字节流
            var msg = ProtoHelper.Instance.ByteArrayParse2IMessage(data);

            if (msg == null)
            {
                Log.Warning("Failed to parse message from Conn {Id}", ConnectionId);
                return;
            }

            _onMessageReceived?.Invoke(ConnectionId, msg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Decode error on Conn {Id}", ConnectionId);
            Close();
        }
    }

    private void OnDisconnectedInternal()
    {
        // 通知统一由 Close 幂等触发，避免主动/被动路径重复回调
        Close();
    }

    public void Send(IMessage message)
    {
        if (_isClosed || message == null) return;

        try
        {
            var bytes = ProtoHelper.Instance.IMessageParse2ByteArray(message);
            if (bytes == null) return;

            var packet = PrependLengthHeader(bytes);

            if (!_sendQueue.Writer.TryWrite(packet))
            {
                ArrayPool<byte>.Shared.Return(packet);
                Log.Error("Send queue full on Conn {Id}", ConnectionId);
                Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Serialize/Send error on Conn {Id}", ConnectionId);
            Close();
        }
    }

    private async Task ProcessSendQueueAsync()
    {
        await foreach (var data in _sendQueue.Reader.ReadAllAsync())
        {
            if (_isClosed)
            {
                // 连接已关闭：不再发送，仅归还池化缓冲区
                ArrayPool<byte>.Shared.Return(data);
                continue;
            }
            try
            {
                int totalLength = BinaryPrimitives.ReadInt32BigEndian(data) + 4;
                await _socket.SendAsync(new ReadOnlyMemory<byte>(data, 0, totalLength), SocketFlags.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Socket send failed on Conn {Id}", ConnectionId);
                Close();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    private byte[] PrependLengthHeader(byte[] data)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4 + data.Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        Array.Copy(data, 0, buffer, 4, data.Length);
        return buffer;
    }

    public void Close()
    {
        if (_isClosed) return;
        _isClosed = true;
        _sendQueue.Writer.TryComplete();
        _decoder.ActiveDisconnection();
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
            _socket.Close();
            _socket.Dispose();
        }
        catch { }
        Log.Debug("Connection {Id} closed", ConnectionId);

        // 无论主动/被动断开都必须通知一次：NetworkService 依赖此回调清理连接池与连接计数
        if (Interlocked.Exchange(ref _disconnectNotified, 1) == 0)
            _onDisconnected?.Invoke(ConnectionId);
    }
}

