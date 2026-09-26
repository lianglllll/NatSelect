---
name: natselect-dev
description: 帮助在 NatSelect 游戏服务端引擎中开发新模块，包括 Actor、网络协议、配置系统等。当用户需要新增 Actor、扩展网络协议、添加配置项或进行游戏服务端功能开发时使用此 Skill。
---

# NatSelect 游戏服务端引擎开发指南

## 项目概述

NatSelect 是基于 .NET 10.0 的 C# 游戏服务端引擎（对标 Skynet），仅提供 Actor 系统、网络通信、定时器、配置加载等基础设施，不包含任何游戏业务逻辑。

**架构定位**：引擎不直接面对客户端——客户端由外部网关承接（对标 MMORPG 的 GameGate），引擎只与外部节点/网关互连，消息经 `NatSelectEnvelope` 内部协议投递到 Actor 邮箱。

**技术栈**: .NET 10.0 / Google.Protobuf / Grpc.Tools / Serilog / YamlDotNet

## 项目结构

项目采用双项目架构，符合框架设计原则（引擎是类库，不是 Main 入口）：

```
NatSelect/
├── NatSelect/              # 引擎核心（类库）
│   ├── Common/             # 通用工具 (Singleton, DataStream, BusinessException)
│   ├── Config/             # YAML 配置加载与热加载
│   │   ├── ConfigLoader.cs # 基础加载
│   │   ├── ConfigManager.cs # 热加载 + 变更通知
│   │   └── Template/       # 配置强类型定义
│   ├── Core/               # Actor 核心框架 + 引擎
│   │   ├── NatSelectEngine.cs   # 引擎统一入口
│   │   ├── Actor.cs             # Actor 基类（调度器驱动）
│   │   ├── ActorContext.cs      # 上下文（含定时器、子Actor管理）
│   │   ├── ActorRef.cs          # 分布式 Actor 引用
│   │   ├── ActorScheduler.cs    # Worker 线程池调度器
│   │   ├── ActorTimer.cs        # 定时器实现
│   │   ├── ActorInfo.cs         # 诊断信息 DTO
│   │   ├── IActorSystem.cs      # ActorSystem 接口
│   │   ├── LocalActorSystem.cs  # 单机版 ActorSystem 实现
│   │   ├── IAMessage.cs         # 消息接口体系
│   │   └── System/              # 引擎内置系统 Actor
│   │       ├── RootActor.cs         # 监督树根（ActorId=1001）
│   │       ├── NameServiceActor.cs  # 名字服务（对应 Skynet .service）
│   │       └── NodeManagerActor.cs  # 节点连接控制面（网关重连编排）
│   ├── Logger/             # Serilog 结构化日志 (NSLogger)
│   └── Network/            # TCP 网络层（节点互连，不面对客户端）
│       ├── NetworkService.cs     # 节点互连中枢（监听 + 主动连接 + Envelope 路由）
│       ├── TcpServerListener.cs  # 集群端口监听器
│       ├── TcpConnection.cs      # TCP 节点连接封装
│       ├── LengthFieldDecoder.cs # 粘包拆包解码器
│       ├── ProtoHelper.cs        # Protobuf 序列化/反序列化 + 协议注册表
│       ├── ProtoIdAttribute.cs   # 协议号标注特性
│       └── Proto/                # 引擎内部协议（仅 NatSelectEnvelope）
├── NatSelectDemo/          # Demo 项目（可执行，命名空间 NatSelectDemo）
│   ├── Demo/               # EchoDemo.cs + EchoActor.cs
│   ├── Program.cs          # Main 入口 + 关闭信号
│   ├── NatSelectDemo.csproj
│   ├── run.bat             # 运行脚本（支持 daemon 模式 -d）
│   └── stop.bat            # 优雅关闭脚本（触发命名事件）
└── NatSelect.sln           # 解决方案文件（包含两个项目）
```

## 引擎使用（NatSelectEngine）

上层业务通过 `NatSelectEngine` 获取所有基础设施能力：

```csharp
using NatSelect.Core;
using Serilog;

// 创建引擎
await using var engine = new NatSelectEngine("Config/config.yaml");

// 启动引擎（自动完成: 日志→配置→Proto注册→ActorSystem→NetworkService→系统Actor→集群监听→网关连接编排）
await engine.StartAsync();

// 创建 Actor（挂载到引擎内置 RootActor 下，形成完整监督树）
var rootContext = engine.ActorSystem.RootContext;
var actorRef = engine.ActorSystem.SpawnActor<MyActor>(rootContext, "my-service");

// 发送消息
await engine.ActorSystem.SendAsync(actorRef, new MyMessage { Text = "hello" });

// 诊断
var tree = ((LocalActorSystem)engine.ActorSystem).GetActorTree();
Log.Information("Total actors: {Count}", engine.ActorSystem.GetTotalActorCount());

// 优雅关闭（Ctrl+C 或 stop.bat 自动触发）
await engine.StopAsync();
```

**启动链**: NSLogger.Init → ConfigLoader → ProtoHelper.AutoRegisterAll → LocalActorSystem → NetworkService → SetNetworkService → InitializeSystemActors（Root/NameService/NodeManager）→ 集群监听 → NodeManager 发起网关连接  
**关闭链**: 停止监听 → 断开节点连接 → 停止 NetworkService → 停止 ActorSystem → Flush 日志

## 引擎内置系统 Actor

引擎启动时自动创建三个系统 Actor（对应 Skynet 的内置服务），挂在 RootActor 下：

| 系统 Actor | ActorId | 对应 Skynet | 职责 |
|---|---|---|---|
| `RootActor` | 1001 | 监督树根 | 所有 Actor 的父节点；子终止与未知消息兜底记录 |
| `NameServiceActor` | 1002 | `.service` | 服务名→ActorRef 注册/查询（消息 `RegisterNameMessage`/`ResolveNameRequest`）；已注册名 `"name-service"`/`"node-manager"` |
| `NodeManagerActor` | 1003 | `cmaster` | 网关连接编排：初始连接、断线检测、指数退避重连（1s 起、封顶 30s）；被动接入节点只记状态不重连 |

**ActorId 段约定**：0=Invalid 哨兵，1-999 系统保留（1=虚拟根），业务 Actor 从 1000 开始。

**关键原则**：NodeManagerActor 是控制面（连接状态/重连），数据面（消息收发）仍由 NetworkService 直通 Actor 邮箱，连接管理不挡消息热路径。

## Actor 开发规范

### 新增 Actor

Actor 由 `ActorScheduler` 的 Worker 线程池调度，不需要手动启动消息循环：

```csharp
using NatSelect.Core;

public sealed class YourActor : Actor
{
    public YourActor(ActorContext context) : base(context, mailboxCapacity: 1024)
    {
        // 初始化逻辑
        // 可设置定时器
        Context.SetTimer(1000, async () => {
            Log.Information("Tick every second");
        }, repeat: true);
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case YourMessage yourMsg:
                // 处理消息
                break;
            default:
                Log.Warning("Unhandled message: {Type}", msg.GetType().Name);
                break;
        }
        return ValueTask.CompletedTask;
    }
}
```

**重要**：构造函数第一个参数必须是 `ActorContext`，额外参数通过 `SpawnActor` 的 `args` 传入。

**协程约束**：`OnReceiveAsync` 内只能 await 引擎异步原语（`Context.SendAsync`/`DelayAsync`/定时器等）；await 原生异步（如 `Task.Delay`、Socket 操作）会导致协程逃逸、Actor 被强制停止。需要原生异步时用 fire-and-forget + 消息回流（参考 `NodeManagerActor.TryConnectAsync`）。

### 消息定义

```csharp
// 所有消息必须实现 IAMessage
public sealed class YourMessage : IAMessage
{
    public ActorRef Sender { get; set; }
    // 业务字段
}

// 需要响应的消息加 IResponseRequired
public sealed class RequestMessage : IResponseRequired { ... }

// 系统消息实现 ISystemMessage（邮箱满时必达，会驱逐最旧普通消息）
public sealed class YourSystemMsg : ISystemMessage { ... }
```

### Actor 生命周期

- **创建**: `engine.ActorSystem.SpawnActor<T>(parent, name, args)` 或 `context.SpawnChild<T>(name, args)`；`SpawnActor` 会自动登记父子关系（诊断树完整性）
- **停止**: `context.StopChildAsync(name)` 或 `engine.ActorSystem.StopActorAsync(ref)`
- **优雅退出**: 发送 `SystemStopMessage`，触发 `DisposeAsync` 清理
- **监视**: 子 Actor 崩溃会通过 `TerminatedMessage` 通知父 Actor
- **状态**: `ActorContext.State` 跟踪 Running → Stopping → Stopped

### 定时器

ActorContext 内置定时器支持，与 Actor 生命周期绑定（Actor 销毁时自动取消）：

```csharp
// 循环定时器
int timerId = Context.SetTimer(1000, async () => { /* 每秒执行 */ }, repeat: true);

// 一次性延迟
int onceId = Context.ScheduleOnce(5000, async () => { /* 5秒后执行一次 */ });

// 取消定时器
await Context.CancelTimer(timerId);
```

### 消息路由

`ActorContext.SendAsync` 自动判断本地/远程：
- 目标 `IsLocal(NodeId)` → 直接投递到本地 Actor 邮箱
- 目标 NodeId 不同 → 通过 `NetworkService.SendRemoteAsync` 走网络

### Actor 引用

`ActorRef` 是不可变值类型 (struct)：
- `NodeId` 集群唯一，`ActorId` 节点内唯一
- `IsLocal(currentNodeId)` 判断本地/远程
- `ActorRef.Invalid` 表示空引用

## 网络协议开发

### 协议分层原则（重要）

- **引擎只定义传输协议**：`Network/Proto/NatSelect.proto` 只允许定义 `NatSelectEnvelope`（内部信封，承载 NodeId/ActorId 路由 + Payload），**禁止添加任何业务消息**。
- **业务协议由上层定义**：业务 proto 放业务/中间件项目，启动时通过 `ProtoHelper.Instance.Register<T>(id)` 显式注入引擎注册表。
- **自检标准**：引擎代码不出现任何业务消息类型名与业务协议号（Envelope=1 除外）。

### 新增业务 Protobuf 消息（上层项目）

1. 在上层项目定义 proto 并编译（Grpc.Tools）
2. 创建 partial class 扩展标注协议号：

```csharp
// 协议编号全局唯一，必须 > 0（1 已被 NatSelectEnvelope 占用）
[ProtoId(100)]
public sealed partial class YourMessage { }
```

3. 启动时注册：`ProtoHelper.Instance.Register<YourMessage>(100);`

### 帧格式（线上字节格式，与 MMORPG Summer 框架一致）

所有 TCP 链路（节点互连）统一帧格式：

- **外层**: `[4字节大端长度][2字节协议编号][Protobuf数据]`（`TcpConnection.PrependLengthHeader` + `LengthFieldDecoder` 粘包拆包）
- **节点间**: 协议编号=1 的 `NatSelectEnvelope`（SenderNodeId/ActorId、TargetNodeId/ActorId、Payload、PayloadType）

### 节点互连管理（NetworkService）

`NetworkService` 是节点互连中枢（不管理客户端）：

- `StartClusterListenAsync(port, maxConnections)` 启动集群端口监听（接受其他节点/网关接入）
- `ConnectToNodeAsync(host, port, nodeId)` 主动连接外部网关，失败返回 false 不抛异常
- **学习式节点注册**：监听接受的新连接初始身份未定（只能收不能发），收到首个 Envelope 后按 `SenderNodeId` 绑定进节点连接池；重复 NodeId 拒绝绑定并断连
- 断线自动清理（幂等）；`SendRemoteAsync` 发送时按目标 NodeId 查连接，找不到则丢消息记日志
- 节点连接只允许 Envelope 内部协议，其他类型消息一律丢弃

## 配置开发

### 新增配置项

1. 在 `Config/Template/ConfigTemplate.cs` 添加属性类：

```csharp
public class YourConfig
{
    [YamlMember(Alias = "your_field")]
    public string YourField { get; set; } = "default";
}
```

2. 在 `ConfigTemplate` 根类添加属性：

```csharp
[YamlMember(Alias = "your_section")]
public YourConfig YourSection { get; set; } = new();
```

3. 更新 `Config/Template/ConfigTemplate.yaml` 模板
4. 本地 `config.yaml` 按需修改（已被 `.gitignore` 忽略）

### 网关连接配置

```yaml
# 网络配置（集群端口：接受其他节点/网关接入，不直接面对客户端）
network:
  listen_port: 9000
  max_connections: 5000

# 网关连接配置（引擎主动连接的外部网关/节点，空数组则纯监听）
gateway:
  # - host: "127.0.0.1"
  #   port: 10400
  #   node_id: 100
```

注意：`gateway` 段在 yaml 中缺失或为空时 YamlDotNet 会反序列化为 null，代码访问 `Config.Gateway` 前需判空。

### 配置热加载

```csharp
// 使用 ConfigManager 实现热加载
var configManager = new ConfigManager<ConfigTemplate>("Config/config.yaml");

// 订阅变更
configManager.OnConfigChanged += (newConfig) => {
    Log.Information("Config changed!");
};

// 手动重载
var newConfig = configManager.Reload();

// 启用文件变更自动监听（可选）
configManager.EnableFileWatcher(debounceMs: 500);
```

## 诊断

```csharp
// 获取 Actor 总数
int count = engine.ActorSystem.GetTotalActorCount();

// 获取 Actor 信息树（含系统 Actor，监督树从 RootActor 起）
var tree = ((LocalActorSystem)engine.ActorSystem).GetActorTree();
foreach (var info in tree)
{
    Log.Information("[{State}] {Name} @ {Path}", info.State, info.Name, info.Path);
}

// 获取单个 Actor 信息
var info = ((LocalActorSystem)engine.ActorSystem).GetActorInfo(actorRef);
```

## 日志规范

日志系统基于 `NSLogger`（Serilog 封装），提供三路输出：
- **控制台**：Debug 级别，开发环境使用
- **滚动文件**：`logs/game-{Date}.log`，Information 级别，保留 30 天
- **结构化 JSON**：`logs/structured-{Date}.json`，对接 ELK

```csharp
// Actor 内部：使用内置 Log 属性（自动注入 ActorPath、ActorId 上下文）
Log.Information("Processing message: {Type}", msg.GetType().Name);

// Actor 外部：使用 Serilog.Log 静态方法
Serilog.Log.Information("System event: {Event}", eventName);

// 手动初始化日志（通常由 NatSelectEngine 自动完成，测试时可能需要手动调用）
NSLogger.Initialize("MyService", "logs");
NSLogger.Shutdown();  // 程序退出时
```

## 编码约定

| 约定 | 说明 |
|------|------|
| 命名空间 | 跟随目录结构，如 `NatSelect.Core`、`NatSelect.Core.System`、`NatSelect.Network` |
| 文件编码 | YAML 文件使用 UTF-8-BOM（防止中文乱码） |
| 单例 | 继承 `Singleton<T>`，通过 `T.Instance` 访问 |
| 对象池 | `DataStream.Allocate()` 获取，`Dispose()` 自动归还池 |
| 异常 | 业务错误抛 `BusinessException`（不中断 Actor），系统错误才停止 Actor |
| Proto 编译 | 使用 Grpc.Tools 集成构建，`csproj` 中 `<Protobuf Include="Network\Proto\*.proto"/>` |
| Proto 注册 | 引擎内 `AutoRegisterAll()` 只注册内部传输协议（Envelope）；业务协议由上层 `Register<T>(id)` 注入 |
| Actor 构造 | 第一参数必须 `ActorContext`，额外参数通过 `args` 传入 |
| 方法长度 | 不超过 40 行，超长拆分小方法 |
| Actor 状态 | 仅在调度上下文访问 Actor 状态（单执行流保证）；跨线程访问需经消息回流 |

## 构建与运行

```bash
# 编译整个解决方案
dotnet build

# 运行（在 NatSelectDemo 目录下，自动编译）
cd NatSelectDemo
run.bat                # 前台运行（默认 Debug）
run.bat Debug -d       # 后台 daemon 模式

# 优雅关闭（配合 run.bat 使用）
stop.bat

# 或直接使用 dotnet CLI
dotnet run --project NatSelectDemo -- [config.yaml路径]
```

## 新增模块 Checklist

```
- [ ] 在对应目录下创建类文件
- [ ] 命名空间匹配目录结构
- [ ] Actor 继承 Actor 并实现 OnReceiveAsync
- [ ] Actor 构造函数第一个参数为 ActorContext
- [ ] 消息实现 IAMessage 接口（系统消息实现 ISystemMessage）
- [ ] OnReceiveAsync 内不 await 原生异步（协程逃逸约束）
- [ ] 业务 Proto 定义在上层项目 + [ProtoId] 标注 + 启动时 Register 注入
- [ ] 引擎 proto 文件不添加业务消息（仅 Envelope）
- [ ] 配置项更新 ConfigTemplate + ConfigTemplate.yaml
- [ ] 验证 dotnet build 通过
```
