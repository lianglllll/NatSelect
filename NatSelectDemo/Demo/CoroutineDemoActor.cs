using NatSelect.Core;
using Serilog;

namespace NatSelectDemo.Demo;

/// <summary>
/// 协程演示 Actor：展示引擎协程原语（DelayAsync / YieldAsync）
/// 长流程挂起期间不占用 Worker，恢复由大队列调度
/// </summary>
public class CoroutineDemoActor : Actor
{
    public CoroutineDemoActor(ActorContext context) : base(context) { }

    protected override async ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case RunFlow _:
                Log.Information("[CoroutineDemo] {Path} Step 1: flow begins (thread {Thread})",
                    Context.Path, Environment.CurrentManagedThreadId);
                await Context.DelayAsync(1000);
                Log.Information("[CoroutineDemo] {Path} Step 2: after 1s delay (thread {Thread})",
                    Context.Path, Environment.CurrentManagedThreadId);
                await Context.YieldAsync();
                Log.Information("[CoroutineDemo] {Path} Step 3: after yield (thread {Thread})",
                    Context.Path, Environment.CurrentManagedThreadId);
                break;
        }
    }
}

/// <summary>
/// 触发协程流程的消息
/// </summary>
public sealed class RunFlow : IAMessage
{
    public ActorRef Sender { get; set; }
}
