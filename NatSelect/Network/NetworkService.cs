using System.Collections.Concurrent;
using System.Net.Sockets;
using Google.Protobuf;
using NatSelect.Core;
using NatSelect.Protobuf.Interval;
using Serilog;

namespace NatSelect.Network;

/// <summary>
/// 全局网络服务 — 节点互连中枢。
/// 引擎不直接面对客户端（客户端由外部网关承接），此处只负责：
/// 1. 集群端口监听：接受其他节点/网关接入
/// 2. 主动连接外部节点（网关）
/// 3. Envelope 内部协议路由：反序列化后投递目标 Actor 邮箱
/// </summary>
public sealed class NetworkService : IAsyncDisposable
{
    // 1. 已确认身份的节点连接池：NodeId -> TcpConnection (线程安全)
    private readonly ConcurrentDictionary<ulong, TcpConnection> _remoteConnections = new();

    // 2. 待确认身份的连接：ConnectionId -> TcpConnection
    //    监听接受的新连接在收到首个 Envelope 前，不在节点连接池中，发送路径无法命中——天然满足"未确认身份只能收不能发"
    private readonly ConcurrentDictionary<long, TcpConnection> _pendingConnections = new();

    // 3. 记录连接是否来自集群监听（断线时回滚监听器计数；主动连接从未计数）
    private readonly ConcurrentDictionary<long, byte> _listenerAccepted = new();

    // 4. 依赖注入
    private readonly ulong _localNodeId;
    private readonly IActorSystem _actorSystem;
    private readonly ProtoHelper _protoHelper;

    // 5. 集群端口监听器
    private TcpServerListener? _listener;

    // 6. 节点连接事件（供上层控制面订阅，如 NodeManagerActor；在 I/O 线程触发，处理器应快速返回）
    public Action<ulong>? OnNodeBound { get; set; }
    public Action<ulong>? OnNodeDisconnectedEvent { get; set; }

    public NetworkService(ulong localNodeId, IActorSystem actorSystem, ProtoHelper protoHelper)
    {
        _localNodeId = localNodeId;
        _actorSystem = actorSystem;
        _protoHelper = protoHelper;

        Log.Information("NetworkService initialized on NodeId: {NodeId}", _localNodeId);
    }

    #region 集群端口监听 (被动接入)

    /// <summary>
    /// 启动集群端口监听，接受其他节点/网关接入
    /// </summary>
    public async Task StartClusterListenAsync(int port, int maxConnections)
    {
        _listener = new TcpServerListener();
        await _listener.StartAsync(port, maxConnections, OnNodeSocketAccepted);
    }

    private void OnNodeSocketAccepted(Socket socket)
    {
        var connection = new TcpConnection(
            socket,
            onMessageReceived: (connId, msg) => HandleNodeMessage(connId, msg),
            onDisconnected: (connId) => HandleNodeDisconnected(connId)
        );

        // 身份待确认：收到首个 Envelope 后按 SenderNodeId 绑定进节点连接池
        _listenerAccepted[connection.ConnectionId] = 0;
        _pendingConnections[connection.ConnectionId] = connection;
        connection.Start();
        Log.Debug("[NetworkService] Node connection accepted: Conn {Id} (identity pending)", connection.ConnectionId);
    }

    #endregion

    #region 主动连接 (连外部节点/网关)

    /// <summary>
    /// 主动连接外部节点（网关）。连接失败仅记日志返回 false，网关未启动时引擎仍可独立运行。
    /// </summary>
    public async Task<bool> ConnectToNodeAsync(string host, int port, ulong nodeId)
    {
        if (_remoteConnections.ContainsKey(nodeId))
        {
            Log.Warning("Already connected to Node {NodeId}, skip duplicate connect", nodeId);
            return true;
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            Log.Warning(ex, "Failed to connect to Node {NodeId} at {Host}:{Port}", nodeId, host, port);
            return false;
        }

        var connection = new TcpConnection(
            socket,
            onMessageReceived: (connId, msg) => HandleNodeMessage(connId, msg),
            onDisconnected: (connId) => HandleNodeDisconnected(connId)
        );
        connection.Start();
        RegisterRemoteNode(nodeId, connection);
        Log.Information("Connected to Node {NodeId} at {Host}:{Port} (Conn {Id})", nodeId, host, port, connection.ConnectionId);
        return true;
    }

    #endregion

    #region 连接管理 (无锁操作)

    public void RegisterRemoteNode(ulong nodeId, TcpConnection connection)
    {
        _remoteConnections.AddOrUpdate(nodeId, connection, (_, existing) =>
        {
            // 替换语义：关闭被替换的旧连接，防止其继续收包造成同 NodeId 消息双路径
            if (!ReferenceEquals(existing, connection))
                existing.Close();
            return connection;
        });
        Log.Information("Registered remote connection to Node: {NodeId}", nodeId);
    }

    public void UnregisterRemoteNode(ulong nodeId)
    {
        if (_remoteConnections.TryRemove(nodeId, out var conn))
        {
            Log.Information("Unregistered remote connection to Node: {NodeId}", nodeId);
            conn.Close();
        }
    }

    #endregion

    #region 节点消息接收 (统一入口)

    private void HandleNodeMessage(long connId, IMessage msg)
    {
        // 节点连接只允许 Envelope 内部协议，其余类型一律丢弃（业务消息必须装在 Envelope 里）
        if (msg is not NatSelectEnvelope envelope)
        {
            Log.Warning("[NetworkService] Non-envelope message from Conn {Id}: {Type} (dropped)", connId, msg.GetType().Name);
            return;
        }

        BindPendingConnection(connId, envelope.SenderNodeId);
        _ = HandleIncomingEnvelopeSafelyAsync(envelope);
    }

    /// <summary>
    /// 学习式节点注册：监听接受的新连接在收到首个 Envelope 后，按 SenderNodeId 绑定进节点连接池。
    /// </summary>
    private void BindPendingConnection(long connId, ulong senderNodeId)
    {
        if (!_pendingConnections.TryGetValue(connId, out var connection))
            return; // 主动连接已注册，或连接已消失

        if (_remoteConnections.TryAdd(senderNodeId, connection))
        {
            _pendingConnections.TryRemove(connId, out _);
            Log.Information("Node {NodeId} bound to Conn {Id}", senderNodeId, connId);
            SafeInvokeEvent(OnNodeBound, senderNodeId);
            return;
        }

        // TryAdd 失败：并发重复消息下本连接可能已绑定，或 NodeId 被其他连接占用
        if (_remoteConnections.TryGetValue(senderNodeId, out var existing) && existing.ConnectionId == connId)
        {
            _pendingConnections.TryRemove(connId, out _);
            return;
        }

        Log.Warning("Duplicate NodeId {NodeId} from Conn {Id}, closing connection", senderNodeId, connId);
        connection.Close();
    }

    private async Task HandleIncomingEnvelopeSafelyAsync(NatSelectEnvelope envelope)
    {
        try
        {
            await HandleIncomingEnvelopeAsync(envelope);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to handle incoming envelope from Node {Sender}", envelope.SenderNodeId);
        }
    }

    private void HandleNodeDisconnected(long connId)
    {
        _pendingConnections.TryRemove(connId, out _);

        // 回滚监听器连接计数（仅对来自监听的连接）
        if (_listenerAccepted.TryRemove(connId, out _))
            _listener?.OnConnectionClosed();

        // 按连接标识清理节点连接池（节点数少，遍历成本可忽略）
        foreach (var kv in _remoteConnections)
        {
            if (kv.Value.ConnectionId != connId) continue;
            if (_remoteConnections.TryRemove(kv.Key, out var conn))
            {
                Log.Information("Node {NodeId} disconnected (Conn {Id})", kv.Key, connId);
                conn.Close(); // 幂等，确保资源释放
                SafeInvokeEvent(OnNodeDisconnectedEvent, kv.Key);
            }
            break;
        }
    }

    /// <summary>
    /// 安全调用节点事件处理器：上层处理器异常不能击穿 I/O 回调路径
    /// </summary>
    private static void SafeInvokeEvent(Action<ulong>? handler, ulong nodeId)
    {
        try
        {
            handler?.Invoke(nodeId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Node event handler failed for Node {NodeId}", nodeId);
        }
    }

    #endregion

    #region 远程发送 (本地 -> 远程节点)

    /// <summary>
    /// 发送消息到远程节点
    /// 由 ActorContext 直接调用，无邮箱排队
    /// </summary>
    public async ValueTask SendRemoteAsync(ActorRef sender, ActorRef target, IAMessage message)
    {
        // 1. 查找连接 (并发读取，无锁)
        if (!_remoteConnections.TryGetValue(target.NodeId, out var connection))
        {
            Log.Error("No connection found for target Node: {NodeId}. Message dropped.", target.NodeId);
            return;
        }

        // 2. 构建协议包
        var typeCode = _protoHelper.Type2Seq(message.GetType());
        if (typeCode < 0) return;

        // ToByteArray 刚生成的数组所有权转移给 ByteString（后续不可再写），省去 CopyFrom 的二次复制
        var payload = UnsafeByteOperations.UnsafeWrap(_protoHelper.Serialize(message));
        var envelope = new NatSelectEnvelope
        {
            SenderNodeId = sender.NodeId,
            SenderActorId = sender.ActorId,
            TargetNodeId = target.NodeId,
            TargetActorId = target.ActorId,
            Payload = payload,
            PayloadType = (ulong)typeCode
        };

        // 3. 直接发送 (TcpConnection 内部有 Channel 队列，这里是写入 Channel，非阻塞)
        try
        {
            connection.Send(envelope);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send message to Node {NodeId}", target.NodeId);
            UnregisterRemoteNode(target.NodeId); // 触发断线清理
        }
    }

    #endregion

    #region 远程接收 (远程 -> 本地)

    /// <summary>
    /// 处理收到的 Envelope 网络包
    /// 由 HandleNodeMessage 统一入口调用
    /// </summary>
    public async ValueTask HandleIncomingEnvelopeAsync(NatSelectEnvelope envelope)
    {
        // 1. 校验目标节点
        if (envelope.TargetNodeId != _localNodeId)
        {
            Log.Warning("Received message for wrong node: {Target}", envelope.TargetNodeId);
            return;
        }

        // 2. 重建引用
        var senderRef = new ActorRef(envelope.SenderNodeId, envelope.SenderActorId);
        var targetRef = new ActorRef(envelope.TargetNodeId, envelope.TargetActorId);

        // 3. 反序列化
        IAMessage? businessMsg;
        try
        {
            businessMsg = _protoHelper.Deserialize(envelope.Payload, envelope.PayloadType);
            if (businessMsg == null) return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Deserialization error for type: {Type}", envelope.PayloadType);
            return;
        }

        // 4. 注入 Sender
        businessMsg.Sender = senderRef;

        // 5. 【关键】直接投递给 ActorSystem
        // 跳过任何中间 Actor，直接进入目标 Actor 的邮箱
        // 这是性能提升的关键！
        await _actorSystem.SendAsync(targetRef, businessMsg);
    }

    #endregion

    public async ValueTask DisposeAsync()
    {
        // 1. 停止监听
        if (_listener != null)
        {
            await _listener.StopAsync();
        }

        // 2. 关闭所有待确认连接
        foreach (var conn in _pendingConnections.Values)
        {
            conn.Close();
        }
        _pendingConnections.Clear();
        _listenerAccepted.Clear();

        // 3. 关闭所有节点连接
        foreach (var conn in _remoteConnections.Values)
        {
            conn.Close();
        }
        _remoteConnections.Clear();

        Log.Information("[NetworkService] Disposed");
    }
}
