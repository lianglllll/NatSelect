using Google.Protobuf;
using NatSelect.Network;
using NatSelect.Core;
using Serilog;
using System.Net.Sockets;
using System.Threading.Channels;

namespace NatSelect.Network;

public class TcpConnection
{
    private readonly Socket _socket;
    private readonly LengthFieldDecoder _decoder;

    // [删除] private readonly EncryptionManager _encryption; 

    private readonly Channel<byte[]> _sendQueue;
    private bool _isClosed = false;

    private readonly Action<long, IMessage> _onMessageReceived;
    private readonly Action<long> _onDisconnected;

    public long ConnectionId => _socket.Handle.ToInt64();

    public TcpConnection(
        Socket socket,
        Action<long, IMessage> onMessageReceived,
        Action<long> onDisconnected)
    {
        _socket = socket;
        _onMessageReceived = onMessageReceived;
        _onDisconnected = onDisconnected;

        // [删除] 加密初始化
        // _encryption = new EncryptionManager();
        // _encryption.Init();

        _sendQueue = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
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
        Close();
        _onDisconnected?.Invoke(ConnectionId);
    }

    public void Send(IMessage message)
    {
        if (_isClosed || message == null) return;

        try
        {
            // 1. 序列化
            var bytes = ProtoHelper.Instance.IMessageParse2ByteArray(message);

            // [删除] 加密步骤
            // var encrypted = _encryption.Encrypt(bytes);

            // 2. 添加长度头 (4 bytes)
            var packet = PrependLengthHeader(bytes);

            // 3. 写入队列
            if (!_sendQueue.Writer.TryWrite(packet))
            {
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
            if (_isClosed) break;
            try
            {
                await _socket.SendAsync(new ReadOnlyMemory<byte>(data), SocketFlags.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Socket send failed on Conn {Id}", ConnectionId);
                Close();
                break;
            }
        }
    }

    private byte[] PrependLengthHeader(byte[] data)
    {
        byte[] buffer = new byte[4 + data.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
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
            if (_socket != null && _socket.Connected)
            {
                _socket.Shutdown(SocketShutdown.Both);
                _socket.Close();
            }
            _socket.Dispose();
        }
        catch { }
        Log.Debug("Connection {Id} closed", ConnectionId);
    }
}

