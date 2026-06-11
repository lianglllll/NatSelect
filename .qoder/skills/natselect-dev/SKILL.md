---
name: natselect-dev
description: 帮助在 NatSelect 游戏服务端引擎中开发新模块，包括 Actor、网络协议、配置系统等。当用户需要新增 Actor、扩展网络协议、添加配置项或进行游戏服务端功能开发时使用此 Skill。
---

# NatSelect 游戏服务端开发指南

## 项目概述

NatSelect 是基于 .NET 8.0 的 C# 游戏服务端引擎，采用 Actor 模型架构，支持分布式多节点部署。

**技术栈**: .NET 8.0 / Google.Protobuf / Grpc.Tools / Serilog / YamlDotNet

## 项目结构

```
NatSelect/
├── Common/           # 通用工具 (Singleton, DataStream, BusinessException)
├── Config/           # YAML 配置加载 (ConfigLoader, ConfigTemplate)
│   └── Template/     # 配置强类型定义
├── Core/             # Actor 核心框架
├── Logger/           # Serilog 结构化日志
└── Network/          # TCP 网络层 + Protobuf 序列化
    └── Proto/        # .proto 协议文件
```

## Actor 开发规范

### 新增 Actor

```csharp
using NatSelect.Core;
using NatSelect.Common;

namespace NatSelect.YourModule;

public sealed class YourActor : Actor
{
    public YourActor(ActorContext context, /* 额外参数 */) 
        : base(context, mailboxCapacity: 1024)
    {
        // 初始化
    }

    protected override async ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case YourMessage yourMsg:
                await HandleYourMessage(yourMsg);
                break;
            default:
                Log.Warning("Unhandled message: {Type}", msg.GetType().Name);
                break;
        }
    }
}
```

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

// 系统消息实现 ISystemMessage
public sealed class YourSystemMsg : ISystemMessage { ... }
```

### Actor 生命周期

- **创建**: 通过 `context.SpawnChild<T>(name, args)` 创建子 Actor
- **停止**: 通过 `context.StopChildAsync(name)` 停止子 Actor
- **优雅退出**: 发送 `SystemStopMessage`，触发 `DisposeAsync` 清理
- **监视**: 子 Actor 崩溃会通过 `TerminatedMessage` 通知父 Actor

### Actor 引用

`ActorRef` 是不可变值类型 (struct)：
- `NodeId` 集群唯一，`ActorId` 节点内唯一
- 用 `IsLocal(currentNodeId)` 判断本地/远程
- 用 `ActorRef.Invalid` 表示空引用

## 网络协议开发

### 新增 Protobuf 消息

1. 在 `Network/Proto/NatSelect.proto` 中添加 message
2. 重新构建项目（Grpc.Tools 自动编译）
3. 在 `ProtoHelper` 中注册协议编号

```csharp
// 启动时注册
ProtoHelper.Instance.Register<YourProtoMessage>(1001); // 编号唯一
```

### 协议格式

网络包结构：`[4字节大端长度][2字节协议编号][Protobuf数据]`

- `LengthFieldDecoder` 负责粘包拆包（大端4字节长度头）
- `ProtoHelper` 负责序列化/反序列化（2字节协议编号 + Protobuf body）

### 跨节点消息

`NetworkService` 管理节点连接池，消息通过 `NatSelectEnvelope` 封装后发送：
- 本地消息：直接通过 `IActorSystem.SendAsync` 投递到 Actor 邮箱
- 远程消息：封装为 `NatSelectEnvelope`，通过 `TcpConnection` 发送

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

## 日志规范

```csharp
// Actor 内部：使用内置 Log 属性（自动带 ActorPath、ActorId 上下文）
Log.Information("Processing message: {Type}", msg.GetType().Name);

// Actor 外部：使用 Serilog.Log 静态方法
Serilog.Log.Information("System event: {Event}", eventName);
```

## 编码约定

| 约定 | 说明 |
|------|------|
| 命名空间 | 跟随目录结构，如 `NatSelect.Core`、`NatSelect.Network` |
| 文件编码 | YAML 文件使用 UTF-8-BOM（防止中文乱码） |
| 单例 | 继承 `Singleton<T>`，通过 `T.Instance` 访问 |
| 对象池 | `DataStream.Allocate()` 获取，`Dispose()` 自动归还池 |
| 异常 | 业务错误抛 `BusinessException`（不中断 Actor），系统错误才停止 Actor |
| Proto 编译 | 使用 Grpc.Tools 集成构建，`csproj` 中 `<Protobuf Include="..."/>` |

## 构建与运行

```bash
dotnet build
dotnet run -- [config.yaml路径]   # 默认 Config/config.yaml
```

## 新增模块 Checklist

```
- [ ] 在对应目录下创建类文件
- [ ] 命名空间匹配目录结构
- [ ] Actor 继承 Actor 并实现 OnReceiveAsync
- [ ] 消息实现 IAMessage 接口
- [ ] Proto 消息在 ProtoHelper 注册编号
- [ ] 配置项更新 ConfigTemplate
- [ ] 验证 dotnet build 通过
```
