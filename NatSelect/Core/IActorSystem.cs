using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatSelect.Core;

/// <summary>
/// Actor系统核心接口（单例，整个服务端仅一个实例）
/// </summary>
public interface IActorSystem : IAsyncDisposable
{
    // Actor生命周期
    ActorRef SpawnActor<T>(ActorContext parent, string name, params object[] args) where T : Actor;
    ValueTask StopActorAsync(ActorRef actorRef);
    bool IsActorAlive(ActorRef actorRef);

    // 消息路由（核心！）
    ValueTask SendAsync(ActorRef target, IAMessage message);

    // 服务注册（游戏高频：匹配服/网关）
    bool RegisterService(string serviceName, ActorRef actorRef);
    bool UnregisterService(string serviceName);
    ActorRef? LookupService(string serviceName);

    // 监视机制（崩溃传播）
    void Watch(ActorRef watcher, ActorRef target);
    void Unwatch(ActorRef watcher, ActorRef target);

    // 诊断（运维必备）
    int GetTotalActorCount();
}