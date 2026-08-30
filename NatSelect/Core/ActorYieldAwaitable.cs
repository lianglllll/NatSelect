using System.Runtime.CompilerServices;

namespace NatSelect.Core;

/// <summary>
/// 引擎协程挂起原语（Actor 业务代码只能 await 引擎提供的 awaitable）
/// 挂起时将协程续延存入 Actor 的续延槽位并执行挂起动作（立即重新入队 / 启动延迟定时器），
/// 恢复必须经过大队列由 Worker 推进，保证执行流始终收敛于调度循环。
/// </summary>
public readonly struct ActorYieldAwaitable : ICriticalNotifyCompletion
{
    private readonly Actor _actor;
    private readonly Action _onSuspend;

    internal ActorYieldAwaitable(Actor actor, Action onSuspend)
    {
        _actor = actor;
        _onSuspend = onSuspend;
    }

    public ActorYieldAwaitable GetAwaiter() => this;

    /// <summary>
    /// 总是挂起：强制把执行权交还调度器
    /// </summary>
    public bool IsCompleted => false;

    public void OnCompleted(Action continuation) =>
        _actor.StoreContinuation(continuation, _onSuspend);

    public void UnsafeOnCompleted(Action continuation) =>
        _actor.StoreContinuation(continuation, _onSuspend);

    public void GetResult() { }
}
