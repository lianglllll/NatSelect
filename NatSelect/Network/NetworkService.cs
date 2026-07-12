using System.Collections.Concurrent;
using System.Net.Sockets;
using Google.Protobuf;
using NatSelect.Core;
using NatSelect.Protobuf.Interval;
using Serilog;

namespace NatSelect.Network
{
    /// <summary>
    /// 全局网络网关 — 连接池 + 协议编解码 + 消息路由中枢。
    /// </summary>
    public sealed class NetworkService : IAsyncDisposable
    {
        // 1. 远程节点连接池：NodeId -> TcpConnection (线程安全)
        private readonly ConcurrentDictionary<ulong, TcpConnection> _remoteConnections = new();

        // 2. 客户端连接池：ConnectionId -> TcpConnection
        private readonly ConcurrentDictionary<long, TcpConnection> _clientConnections = new();

        // 3. 依赖注入
        private readonly ulong _localNodeId;
        private readonly IActorSystem _actorSystem;
        private readonly ProtoHelper _protoHelper;

        // 4. TCP 服务端监听器
        private TcpServerListener? _listener;

        // 5. 上层回调
        public Func<TcpConnection, ValueTask>? OnClientConnected { get; set; }
        public Action<TcpConnection>? OnClientDisconnected { get; set; }

        public int ClientConnectionCount => _clientConnections.Count;

        public NetworkService(ulong localNodeId, IActorSystem actorSystem, ProtoHelper protoHelper)
        {
            _localNodeId = localNodeId;
            _actorSystem = actorSystem;
            _protoHelper = protoHelper;

            Log.Information("NetworkService initialized on NodeId: {NodeId}", _localNodeId);
        }

        #region TCP 服务端监听

        /// <summary>
        /// 启动 TCP 监听
        /// </summary>
        public async Task StartListenAsync(int port, int maxConnections)
        {
            _listener = new TcpServerListener();
            await _listener.StartAsync(port, maxConnections, OnClientSocketAccepted);
        }

        private void OnClientSocketAccepted(Socket socket)
        {
            var connection = new TcpConnection(
                socket,
                onMessageReceived: (connId, msg) => HandleClientMessageAsync(connId, msg),
                onDisconnected: (connId) => HandleClientDisconnected(connId)
            );

            var connId = connection.ConnectionId;
            if (_clientConnections.TryAdd(connId, connection))
            {
                connection.Start();
                Log.Debug("[NetworkService] Client {ConnId} registered", connId);

                // 通知上层
                OnClientConnected?.Invoke(connection);
            }
        }

        private void HandleClientMessageAsync(long connId, Google.Protobuf.IMessage msg)
        {
            // 客户端消息处理：由上层通过 OnClientConnected 注册的回调处理
            // 这里暂时只做日志记录，具体路由由业务层决定
            Log.Debug("[NetworkService] Received client message from {ConnId}: {Type}", connId, msg.GetType().Name);
        }

        private void HandleClientDisconnected(long connId)
        {
            if (_clientConnections.TryRemove(connId, out var connection))
            {
                Log.Debug("[NetworkService] Client {ConnId} disconnected", connId);
                _listener?.OnConnectionClosed();

                // 通知上层
                OnClientDisconnected?.Invoke(connection);
            }
        }

        #endregion

        #region 连接管理 (无锁操作)

        public void RegisterRemoteNode(ulong nodeId, TcpConnection connection)
        {
            _remoteConnections.AddOrUpdate(nodeId, connection, (_, _) => connection);
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

        /// <summary>
        /// 向指定客户端发送消息
        /// </summary>
        public void SendToClient(long connId, Google.Protobuf.IMessage message)
        {
            if (_clientConnections.TryGetValue(connId, out var connection))
            {
                connection.Send(message);
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
            var payloadType = (ulong)_protoHelper.Type2Seq(message.GetType());
            var envelope = new NatSelectEnvelope
            {
                SenderNodeId = sender.NodeId,
                SenderActorId = sender.ActorId,
                TargetNodeId = target.NodeId,
                TargetActorId = target.ActorId,
                Payload = ByteString.CopyFrom(_protoHelper.Serialize(message)),
                PayloadType = payloadType
            };

            // 3. 直接发送 (TcpConnection 内部有 Channel 队列，这里是写入 Channel，非阻塞)
            try
            {
                connection.Send(envelope);
                // Log.Debug("Sent remote msg: {Type} to {Target}", message.GetType().Name, target);
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
        /// 处理收到的网络包
        /// 由 TcpConnection 回调直接调用
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

            // Log.Debug("Received remote msg: {Type} from {Sender}", businessMsg.GetType().Name, senderRef);
        }

        #endregion

        public async ValueTask DisposeAsync()
        {
            // 1. 停止监听
            if (_listener != null)
            {
                await _listener.StopAsync();
            }

            // 2. 关闭所有客户端连接
            foreach (var conn in _clientConnections.Values)
            {
                conn.Close();
            }
            _clientConnections.Clear();

            // 3. 关闭所有远程节点连接
            foreach (var conn in _remoteConnections.Values)
            {
                conn.Close();
            }
            _remoteConnections.Clear();

            Log.Information("[NetworkService] Disposed");
        }
    }
}
