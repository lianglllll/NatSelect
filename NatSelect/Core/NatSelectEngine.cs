using NatSelect.Config;
using NatSelect.Config.Template;
using NatSelect.Logger;
using NatSelect.Network;
using Serilog;

namespace NatSelect.Core;

/// <summary>
/// NatSelect 引擎核心入口
/// 统一管理启动/关闭生命周期，上层通过此引擎获取所有基础设施能力
/// </summary>
public sealed class NatSelectEngine : IAsyncDisposable
{
    public enum EngineState { Stopped, Starting, Running, Stopping }

    private EngineState _state = EngineState.Stopped;
    private readonly string _configPath;

    // 核心组件
    public ConfigTemplate Config { get; private set; } = new();
    public IActorSystem ActorSystem { get; private set; } = null!;
    public NetworkService? NetworkService { get; private set; }
    public ulong NodeId { get; private set; }

    // 上层回调：客户端连接/断开事件
    public Func<TcpConnection, ValueTask>? OnClientConnected { get; set; }
    public Action<TcpConnection>? OnClientDisconnected { get; set; }

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
            }

            // 6.6 注册客户端连接回调
            if (OnClientConnected != null)
                NetworkService.OnClientConnected = OnClientConnected;
            if (OnClientDisconnected != null)
                NetworkService.OnClientDisconnected = OnClientDisconnected;

            Log.Information("NetworkService created (Port: {Port})", Config.Network.ListenPort);

            // 7. 启动 TCP 监听
            await NetworkService.StartListenAsync(Config.Network.ListenPort, Config.Network.MaxConnections);
            Log.Information("TCP Server listening on port {Port}", Config.Network.ListenPort);

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
        // 从配置读取或使用默认值
        string logDir = "logs";
        string serviceName = "NatSelect";

        NSLogger.Initialize(serviceName, logDir);
    }

    private void LoadConfig()
    {
        if (!File.Exists(_configPath))
        {
            Log.Warning("Config file not found at {Path}, using defaults.", _configPath);
            Config = new ConfigTemplate();
            return;
        }

        Config = ConfigLoader.Load<ConfigTemplate>(_configPath);
    }

    private ulong GenerateNodeId()
    {
        // 简单实现：基于机器名哈希 + 端口号，保证同一机器不同端口有不同 ID
        var nameHash = (ulong)_configPath.GetHashCode();
        var portHash = (ulong)Config.Network.ListenPort;
        return (nameHash << 32) | portHash;
    }
}
