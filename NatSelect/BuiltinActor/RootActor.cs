using NatSelect.Core;

namespace NatSelect.BuiltinActor;

/// <summary>
/// 引擎内置根 Actor：监督树的地基。
/// 所有系统 Actor 与上层业务 Actor 均挂载于此；子 Actor 终止与未识别消息在此兜底记录。
/// </summary>
public sealed class RootActor : Actor
{
    public RootActor(ActorContext context) : base(context, mailboxCapacity: 1024)
    {
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case TerminatedMessage terminated:
                Log.Warning("Child actor terminated: {Ref}", terminated.ActorRef);
                break;

            default:
                Log.Warning("Unhandled message at root: {Type} from {Sender}", msg.GetType().Name, msg.Sender);
                break;
        }
        return ValueTask.CompletedTask;
    }
}
