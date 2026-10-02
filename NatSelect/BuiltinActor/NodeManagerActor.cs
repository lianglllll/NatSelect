using NatSelect.Config.Template;
using NatSelect.Core;
using NatSelect.Network;

namespace NatSelect.BuiltinActor;

/// <summary>
/// 节点连接管理 Actor（控制面，对应 Skynet 的 cmaster/harbor 管理）。
/// 职责：维护节点连接状态、对主动连接目标（网关）做断线重连调度（指数退避）。
/// 数据面（消息收发）不经过本 Actor，仍由 NetworkService 直通目标邮箱。
/// </summary>
public sealed class NodeManagerActor : Actor
{
    private readonly NetworkService _networkService;

    // 仅在本 Actor 调度上下文访问（单执行流，无需并发容器）
    private readonly Dictionary<ulong, GatewayNodeConfig> _gateways = new();
    private readonly Dictionary<ulong, NodeLinkState> _links = new();

    // 重连退避参数（毫秒）
    private const int InitialRetryDelayMs = 1000;
    private const int MaxRetryDelayMs = 30000;

    public NodeManagerActor(ActorContext context, List<GatewayNodeConfig> gateways, NetworkService networkService)
        : base(context, mailboxCapacity: 1024)
    {
        _networkService = networkService;

        foreach (var gw in gateways ?? new List<GatewayNodeConfig>())
        {
            if (string.IsNullOrWhiteSpace(gw.Host) || gw.Port <= 0 || gw.NodeId == 0) continue;
            _gateways[gw.NodeId] = gw;
            _links[gw.NodeId] = new NodeLinkState();
        }
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case StartManageMessage:
                HandleStart();
                break;

            case NodeConnectedMessage connected:
                HandleConnected(connected);
                break;

            case NodeConnectFailedMessage failed:
                HandleConnectFailed(failed);
                break;

            case NodeDisconnectedMessage disconnected:
                HandleDisconnected(disconnected);
                break;

            case NodeBoundMessage bound:
                HandleBound(bound);
                break;

            default:
                Log.Warning("Unhandled message: {Type}", msg.GetType().Name);
                break;
        }
        return ValueTask.CompletedTask;
    }

    private void HandleStart()
    {
        foreach (var gw in _gateways.Values)
            BeginConnect(gw);
    }

    private void HandleConnected(NodeConnectedMessage connected)
    {
        if (_links.TryGetValue(connected.NodeId, out var state))
        {
            state.Connected = true;
            state.Connecting = false;
            state.RetryCount = 0;
            Log.Information("Node {NodeId} connected", connected.NodeId);
        }
    }

    private void HandleConnectFailed(NodeConnectFailedMessage failed)
    {
        if (_links.TryGetValue(failed.NodeId, out var state))
        {
            state.Connected = false;
            state.Connecting = false;
            ScheduleRetry(failed.NodeId, state);
        }
    }

    private void HandleDisconnected(NodeDisconnectedMessage disconnected)
    {
        if (_links.TryGetValue(disconnected.NodeId, out var state))
        {
            state.Connected = false;
            state.Connecting = false;
            // 仅对主动连接目标（网关）重连；被动接入的节点由对端自行重连
            if (_gateways.ContainsKey(disconnected.NodeId))
                ScheduleRetry(disconnected.NodeId, state);
        }
    }

    private void HandleBound(NodeBoundMessage bound)
    {
        // 被动接入（监听端口）的节点：仅记录状态，不做重连管理
        if (!_links.TryGetValue(bound.NodeId, out var state))
        {
            state = new NodeLinkState();
            _links[bound.NodeId] = state;
        }
        state.Connected = true;
        state.Connecting = false;
        Log.Information("Passive node {NodeId} bound", bound.NodeId);
    }

    private void BeginConnect(GatewayNodeConfig gw)
    {
        var state = _links[gw.NodeId];
        if (state.Connected || state.Connecting) return;
        state.Connecting = true;
        _ = TryConnectAsync(gw);
    }

    /// <summary>
    /// 在线程池执行连接（fire-and-forget），结果以消息回流自身邮箱，
    /// 保证状态读写始终在 Actor 调度上下文内。回调在 OnReceiveAsync 外执行，不受协程逃逸约束。
    /// </summary>
    private async Task TryConnectAsync(GatewayNodeConfig gw)
    {
        bool success;
        try
        {
            success = await _networkService.ConnectToNodeAsync(gw.Host, gw.Port, gw.NodeId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unexpected error connecting to Node {NodeId}", gw.NodeId);
            success = false;
        }

        _ = TellAsync(success
            ? new NodeConnectedMessage { NodeId = gw.NodeId }
            : new NodeConnectFailedMessage { NodeId = gw.NodeId, Host = gw.Host, Port = gw.Port });
    }

    private void ScheduleRetry(ulong nodeId, NodeLinkState state)
    {
        if (state.RetryPending) return;
        state.RetryPending = true;
        var delay = Math.Min(MaxRetryDelayMs, InitialRetryDelayMs << Math.Min(state.RetryCount, 10));
        state.RetryCount++;

        Context.ScheduleOnce(delay, () =>
        {
            state.RetryPending = false;
            if (state.Connected) return ValueTask.CompletedTask;
            if (_gateways.TryGetValue(nodeId, out var gw))
                BeginConnect(gw);
            return ValueTask.CompletedTask;
        });
    }

    /// <summary>节点链路状态（仅调度上下文访问）</summary>
    private sealed class NodeLinkState
    {
        public bool Connected;
        public bool Connecting;
        public bool RetryPending;
        public int RetryCount;
    }
}

/// <summary>开始管理：发起初始连接（由引擎初始化系统 Actor 时发送）</summary>
public sealed class StartManageMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
}

/// <summary>节点连接成功（主动连接结果回流）</summary>
public sealed class NodeConnectedMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public ulong NodeId { get; set; }
}

/// <summary>节点连接失败（主动连接结果回流）</summary>
public sealed class NodeConnectFailedMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public ulong NodeId { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>节点断开（NetworkService 事件回流）</summary>
public sealed class NodeDisconnectedMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public ulong NodeId { get; set; }
}

/// <summary>被动节点绑定成功（监听接入后学习式注册回流）</summary>
public sealed class NodeBoundMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public ulong NodeId { get; set; }
}
