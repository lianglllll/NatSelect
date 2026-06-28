---
name: natselect-conventions
description: NatSelect 项目编码规范，确保所有代码符合 Actor 模型、网络协议、配置系统的特定约定
alwaysApply: true
---

# NatSelect 项目编码规范

本规则确保所有代码变更严格遵守 NatSelect 游戏服务端引擎的编码约定。

---

## 命名空间规范

命名空间必须**跟随目录结构**，严格对应：

| 目录 | 命名空间 |
|------|---------|
| `NatSelect/Common/` | `NatSelect.Common` |
| `NatSelect/Core/` | `NatSelect.Core` |
| `NatSelect/Network/` | `NatSelect.Network` |
| `NatSelect/Config/` | `NatSelect.Config` |
| `NatSelect/Logger/` | `NatSelect.Logger` |
| `NatSelect/Network/Proto/` | `NatSelect.Protobuf.Interval` |
| `NatSelectDemo/` | `NatSelectDemo` |
| `NatSelectDemo/Demo/` | `NatSelectDemo.Demo` |

---

## Actor 开发规范

### 构造函数
```csharp
// ✅ 正确：第一参数必须是 ActorContext
public MyActor(ActorContext context) : base(context, mailboxCapacity: 1024) { }
public MyActor(ActorContext context, string param1) : base(context) { }

// ❌ 错误：缺少 ActorContext 参数
public MyActor(string param1) : base(...) { }
```

### 消息处理
```csharp
// ✅ 正确：使用 switch 模式匹配
protected override ValueTask OnReceiveAsync(IAMessage msg)
{
    switch (msg)
    {
        case MyMessage myMsg:
            // 处理
            break;
        default:
            Log.Warning("Unhandled: {Type}", msg.GetType().Name);
            break;
    }
    return ValueTask.CompletedTask;
}
```

### 消息定义
- 所有消息必须实现 `IAMessage` 接口
- 需要响应的消息加 `IResponseRequired`
- 系统消息实现 `ISystemMessage`

---

## Proto 协议规范

1. 在 `Network/Proto/NatSelect.proto` 中定义 message
2. 在 `Network/Proto/` 下创建 partial class 扩展，标注 `[ProtoId(n)]`
3. 协议编号全局唯一，必须 > 0
4. 构建时 Grpc.Tools 自动编译 proto

---

## 配置规范

- 配置类添加 `[YamlMember(Alias = "xxx")]` 特性
- 修改 `ConfigTemplate.cs` 后必须同步更新 `ConfigTemplate.yaml`
- YAML 文件编码使用 **UTF-8-BOM**（防止中文乱码）

---

## 通用规范

| 规范 | 说明 |
|------|------|
| 单例 | 继承 `Singleton<T>`，通过 `T.Instance` 访问 |
| 对象池 | `DataStream.Allocate()` 获取，`Dispose()` 归还 |
| 异常 | 业务错误抛 `BusinessException`，系统错误才停止 Actor |
| 日志 | Actor 内用 `Log` 属性，Actor 外用 `Serilog.Log` |
| 入口 | `Program.cs` 的 Main 方法写在 `Program` 类中 |
