using NatSelect.Core;
using Serilog;

namespace NatSelect;

/// <summary>
/// 简单的 Echo Actor 示例
/// 接收消息并回复，同时展示定时器用法
/// </summary>
public class EchoActor : Actor
{
    private int _messageCount = 0;
    private int _timerId;

    public EchoActor(ActorContext context) : base(context, mailboxCapacity: 1024)
    {
        Log.Information("[EchoActor] Created at {Path}", Context.Path);

        // 设置一个每 3 秒触发的循环定时器
        _timerId = Context.SetTimer(3000, async () =>
        {
            Log.Information("[EchoActor] Timer tick! Processed {Count} messages so far.", _messageCount);
        }, repeat: true);
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case PingMessage ping:
                _messageCount++;
                Log.Information("[EchoActor] Received ping #{Count}: {Text}", _messageCount, ping.Text);

                // 回复 Pong
                if (ping.Sender.IsValid)
                {
                    _ = Context.SendAsync(ping.Sender, new PongMessage
                    {
                        Text = $"Echo: {ping.Text}",
                        Count = _messageCount
                    });
                }
                break;

            case PongMessage pong:
                Log.Information("[EchoActor] Received pong: {Text} (count: {Count})", pong.Text, pong.Count);
                break;

            default:
                Log.Warning("[EchoActor] Unknown message type: {Type}", msg.GetType().Name);
                break;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Ping 消息
/// </summary>
public class PingMessage : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// Pong 消息
/// </summary>
public class PongMessage : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Text { get; set; } = "";
    public int Count { get; set; }
}
