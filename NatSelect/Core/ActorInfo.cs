namespace NatSelect.Core;

/// <summary>
/// Actor 运行状态
/// </summary>
public enum ActorState
{
    Created,
    Running,
    Stopping,
    Stopped,
    Error
}

/// <summary>
/// Actor 状态快照（只读，用于诊断）
/// </summary>
public record ActorInfo(
    ActorRef Self,
    string Name,
    string Path,
    ActorState State,
    int MailboxSize,
    IReadOnlyList<ActorInfo> Children
);
