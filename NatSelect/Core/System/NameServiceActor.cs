using NatSelect.Core;

namespace NatSelect.Core.System;

/// <summary>
/// 名字服务 Actor：服务名 -> ActorRef 的注册与查询权威通道（对应 Skynet 的 .service 服务）。
/// 注册/查询消息为系统消息（必达）；本地同步快速路径仍可直接使用 IActorSystem.LookupService。
/// </summary>
public sealed class NameServiceActor : Actor
{
    private readonly IActorSystem _system;

    public NameServiceActor(ActorContext context, IActorSystem system) : base(context, mailboxCapacity: 1024)
    {
        _system = system;
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case RegisterNameMessage register:
                if (_system.RegisterService(register.Name, register.ActorRef))
                    Log.Information("Service registered: {Name} -> {Ref}", register.Name, register.ActorRef);
                else
                    Log.Warning("Service name already registered: {Name}", register.Name);
                break;

            case UnregisterNameMessage unregister:
                _system.UnregisterService(unregister.Name);
                Log.Information("Service unregistered: {Name}", unregister.Name);
                break;

            case ResolveNameRequest request:
                var resolved = _system.LookupService(request.Name);
                if (request.Sender.IsValid)
                {
                    _ = Context.SendAsync(request.Sender, new ResolveNameResponse
                    {
                        Name = request.Name,
                        ActorRef = resolved ?? ActorRef.Invalid
                    });
                }
                break;

            default:
                Log.Warning("Unhandled message: {Type}", msg.GetType().Name);
                break;
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>注册服务名（已存在则拒绝并记录警告）</summary>
public sealed class RegisterNameMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public string Name { get; set; } = "";
    public ActorRef ActorRef { get; set; }
}

/// <summary>注销服务名</summary>
public sealed class UnregisterNameMessage : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>按名字解析服务（需要响应）</summary>
public sealed class ResolveNameRequest : ISystemMessage, IResponseRequired
{
    public ActorRef Sender { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>名字解析响应（ActorRef.Invalid 表示不存在）</summary>
public sealed class ResolveNameResponse : ISystemMessage
{
    public ActorRef Sender { get; set; }
    public string Name { get; set; } = "";
    public ActorRef ActorRef { get; set; }
}
