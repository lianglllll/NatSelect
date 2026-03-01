using System.Collections.Concurrent;
using Google.Protobuf;
using NatSelect.Core;
using Serilog;
using NatSelect.Protobuf.Interval;

namespace NatSelect.Network
{
    /// <summary>
    /// 全局网络管理服务 (非 Actor!)
    /// 职责：维护节点连接池，处理协议编解码，直接路由消息到 ActorSystem
    /// 特点：无锁高并发，无邮箱排队延迟
    /// </summary>
    public sealed class NetworkService : IAsyncDisposable
    {
        // 1. 远程连接池：NodeId -> TcpConnection (线程安全)
        private readonly ConcurrentDictionary<ulong, TcpConnection> _remoteConnections = new();

        // 2. 依赖注入
        private readonly ulong _localNodeId;
        private readonly IActorSystem _actorSystem;
        private readonly ProtoHelper _protoHelper;

        public NetworkService(ulong localNodeId, IActorSystem actorSystem, ProtoHelper protoHelper)
        {
            _localNodeId = localNodeId;
            _actorSystem = actorSystem;
            _protoHelper = protoHelper;

            Log.Information("NetworkService initialized on NodeId: {NodeId}", _localNodeId);
        }

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

        #endregion

        #region 远程发送 (本地 -> 远程)

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
            var envelope = new NatSelectEnvelope
            {
                SenderNodeId = sender.NodeId,
                SenderActorId = sender.ActorId,
                TargetNodeId = target.NodeId,
                TargetActorId = target.ActorId,
                Payload = ByteString.CopyFrom(_protoHelper.Serialize(message))
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
            foreach (var conn in _remoteConnections.Values)
            {
                conn.Close();
            }
            _remoteConnections.Clear();
            await Task.CompletedTask;
        }
    }
}