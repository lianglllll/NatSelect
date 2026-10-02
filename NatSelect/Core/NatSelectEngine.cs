using System.Security.Cryptography;
using System.Text;
using NatSelect.BuiltinActor;
using NatSelect.Config;
using NatSelect.Config.Template;
using NatSelect.Logger;
using NatSelect.Network;
using Serilog;

namespace NatSelect.Core;

/// <summary>
/// NatSelect 引擎核心入口
/// 统一管理启动/关闭生命周期，上层通过此引擎获取所有基础设施能力。
/// 引擎只与外部节点/网关互连（不直接面对客户端），客户端协议由外部网关承接。
/// </summary>
public sealed class NatSelectEngine : IAsyncDisposable
{
    public enum EngineState { 
        Stopped, 
        Starting, 
        Running, 
        Stopping 
    }

    // 状态由 StartAsync/StopAsync 写入、外部线程通过 State 属性读取，volatile 保证可见性
    private volatile EngineState _state = EngineState.Stopped;
    private readonly string _configPath;

    // 核心组件
    public ulong NodeId { get; private set; }
    public ConfigTemplate Config { get; private set; } = new();
    public IActorSystem ActorSystem { get; private set; } = null!;
    public NetworkService? NetworkService { get; private set; }

    public EngineState State => _state;

    public NatSelectEngine(string configPath = "Config/config.yaml")
    {
        _configPath = configPath;
    }

    /// <summary>
    /// 启动引擎（完整启动链）
    /// </summary>
    public async Task StartAsync()
    {
        if (_state != EngineState.Stopped)
            throw new InvalidOperationException($"Cannot start engine in state: {_state}");

        _state = EngineState.Starting;

        try
        {
            // 1. 初始化日志
            InitializeLogger();

            Log.Information("=========================================");
            Log.Information("NatSelect Engine v1.0 Starting...");
            Log.Information("=========================================");

            // 2. 加载配置
            LoadConfig();

            // 3. 生成 NodeId（基于配置或随机）
            NodeId = GenerateNodeId();
            Log.Information("Node ID : {NodeId}", NodeId);

            // 4. 注册 Proto 消息
            ProtoHelper.Instance.AutoRegisterAll();

            // 5. 创建 ActorSystem
            ActorSystem = new LocalActorSystem(NodeId, Config.Node.ActorSystem);
            Log.Information("ActorSystem created (Workers: {Workers})", Config.Node.ActorSystem.WorkerThreads);

            // 6. 创建 NetworkService
            NetworkService = new NetworkService(NodeId, ActorSystem, ProtoHelper.Instance);

            // 6.5 将 NetworkService 注入 ActorSystem，实现跨节点消息路由
            if (ActorSystem is LocalActorSystem localSystem)
            {
                localSystem.SetNetworkService(NetworkService);

                // 6.6 初始化引擎内置系统 Actor（RootActor/NameServiceActor/NodeManagerActor）
                // 网关连接由 NodeManagerActor 编排（含失败重连），引擎不再直接连接
                InitializeSystemActors(localSystem, Config.Gateway ?? new List<GatewayNodeConfig>(), NetworkService);
            }

            Log.Information("NetworkService created (ClusterPort: {Port})", Config.Network.ListenPort);

            // 7. 启动集群端口监听（接受其他节点/网关接入）
            await NetworkService.StartClusterListenAsync(Config.Network.ListenPort, Config.Network.MaxConnections);
            Log.Information("Cluster listening on port {Port}", Config.Network.ListenPort);

            // 8. 标记运行中
            _state = EngineState.Running;

            Log.Information("=========================================");
            Log.Information("NatSelect Engine Started Successfully!");
            Log.Information("=========================================");
        }
        catch (Exception ex)
        {
            _state = EngineState.Stopped;
            Log.Fatal(ex, "Engine startup failed!");
            await RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 优雅关闭
    /// </summary>
    public async Task StopAsync()
    {
        if (_state != EngineState.Running)
            return;

        _state = EngineState.Stopping;
        Log.Information("NatSelect Engine stopping...");

        // 1. 停止网络（先停止接受连接，再断开现有连接）
        if (NetworkService != null)
        {
            await NetworkService.DisposeAsync();
            Log.Information("NetworkService stopped.");
        }

        // 2. 停止 ActorSystem
        if (ActorSystem != null)
        {
            await ActorSystem.DisposeAsync();
            Log.Information("ActorSystem stopped.");
        }

        _state = EngineState.Stopped;
        Log.Information("NatSelect Engine stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        NSLogger.Shutdown();
    }

    private void InitializeLogger()
    {
        // 当前日志目录与服务名固定（配置日志段尚未引入，此处不要写"从配置读取"误导）
        string logDir = "logs";
        string serviceName = "NatSelect";

        NSLogger.Initialize(serviceName, logDir);
    }

    private void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            Log.Error("Config file not found at {Path}!", _configPath);
            throw new FileNotFoundException($"配置文件未找到: {_configPath}");
        }

        Config = ConfigLoader.Load<ConfigTemplate>(_configPath);
    }

    /// <summary>
    /// 初始化引擎内置系统 Actor：RootActor（监督树根）、NameServiceActor（名字服务）、NodeManagerActor（节点连接管理）。
    /// 在 NetworkService 创建并注入后调用一次；连接事件接线到 NodeManagerActor 邮箱。
    /// </summary>
    private void InitializeSystemActors(LocalActorSystem system, List<GatewayNodeConfig> gateways, NetworkService networkService)
    {
        // 虚拟根上下文：ActorId=1 为保留的系统虚拟根（不注册进 ActorSystem）
        var virtualRootContext = new ActorContext(system, new ActorRef(NodeId, 1), null, "/");

        var rootRef = system.SpawnActor<RootActor>(virtualRootContext, "root");
        system.RootActorRef = rootRef;
        system.SetRootContext(rootRef);

        var nameServiceRef = system.SpawnActor<NameServiceActor>(system.RootContext, "name-service", system);
        system.NameServiceRef = nameServiceRef;
        system.RegisterService("name-service", nameServiceRef);

        var nodeManagerRef = system.SpawnActor<NodeManagerActor>(system.RootContext, "node-manager", gateways, networkService);
        system.NodeManagerRef = nodeManagerRef;
        system.RegisterService("node-manager", nodeManagerRef);

        // 接线：NetworkService 连接事件 -> NodeManagerActor 邮箱（事件在 I/O 线程触发，投递线程安全）
        networkService.OnNodeBound = nodeId =>
            _ = system.SendAsync(nodeManagerRef, new NodeBoundMessage { NodeId = nodeId });
        networkService.OnNodeDisconnectedEvent = nodeId =>
            _ = system.SendAsync(nodeManagerRef, new NodeDisconnectedMessage { NodeId = nodeId });

        // 发起初始连接管理（网关连接由 NodeManagerActor 编排，含失败重连）
        _ = system.SendAsync(nodeManagerRef, new StartManageMessage());

        Log.Information("[Engine] System actors initialized (root: {Root}, name-service: {Name}, node-manager: {Node})",
            rootRef, nameServiceRef, nodeManagerRef);
    }

    private async Task RollbackAsync()
    {
        // 启动失败时释放已启动的组件（调度器 Worker 线程等），避免残留后台线程
        try
        {
            if (NetworkService != null) await NetworkService.DisposeAsync();
            if (ActorSystem != null) await ActorSystem.DisposeAsync();
        }
        catch (Exception cleanupEx)
        {
            Log.Error(cleanupEx, "Cleanup after startup failure failed");
        }
    }

    private ulong GenerateNodeId()
    {
        // 用 SHA256 生成稳定身份：string.GetHashCode 是进程级随机化的，会导致 NodeId 跨重启漂移
        var identity = $"{Environment.MachineName}:{Config.Network.ListenPort}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return BitConverter.ToUInt64(hash, 0);
    }
}
