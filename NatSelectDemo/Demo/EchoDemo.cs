using NatSelect.Core;
using Serilog;

namespace NatSelectDemo.Demo;

/// <summary>
/// Echo 演示程序
/// 展示 Actor 创建、消息通信、定时器、诊断等核心能力
/// </summary>
public static class EchoDemo
{
    public static async Task RunAsync(NatSelectEngine engine)
    {
        Log.Information("========== NatSelect Demo Starting ==========");

        // 1. 创建根上下文（所有 Demo Actor 的父节点）
        var rootContext = new ActorContext(
            engine.ActorSystem,
            new ActorRef(engine.NodeId, 999999),
            parent: null,
            path: "/");

        // 2. 创建两个 EchoActor
        var echo1Ref = engine.ActorSystem.SpawnActor<EchoActor>(rootContext, "echo-1");
        var echo2Ref = engine.ActorSystem.SpawnActor<EchoActor>(rootContext, "echo-2");

        Log.Information("Created EchoActor 1: {Ref}", echo1Ref);
        Log.Information("Created EchoActor 2: {Ref}", echo2Ref);

        // 3. 发送消息演示 Actor 间通信
        for (int i = 1; i <= 5; i++)
        {
            await engine.ActorSystem.SendAsync(echo1Ref, new PingMessage
            {
                Sender = echo2Ref,
                Text = $"Hello from demo #{i}"
            });

            await Task.Delay(100);
        }

        // 4. 等待消息处理完成
        await Task.Delay(500);

        // 5. 打印诊断信息
        PrintDiagnostics(engine);

        Log.Information("========== Demo Complete ==========");
    }

    private static void PrintDiagnostics(NatSelectEngine engine)
    {
        Log.Information("========== Actor System Diagnostics ==========");
        Log.Information("Total Actors: {Count}", engine.ActorSystem.GetTotalActorCount());

        if (engine.ActorSystem is LocalActorSystem localSystem)
        {
            var tree = localSystem.GetActorTree();
            Log.Information("Actor Tree ({Count} roots):", tree.Count);

            foreach (var snapshot in tree)
            {
                PrintActorSnapshot(snapshot, "");
            }
        }
    }

    private static void PrintActorSnapshot(ActorSnapshot snapshot, string indent)
    {
        Log.Information("{Indent}[{State}] {Name} @ {Path} (mailbox: {MailboxSize}/{MailboxCapacity}, dropped: {Dropped}, mergedTicks: {Merged}, children: {ChildCount})",
            indent, snapshot.State, snapshot.Name, snapshot.Path,
            snapshot.MailboxSize, snapshot.MailboxCapacity,
            snapshot.DroppedMessageCount, snapshot.MergedTickCount,
            snapshot.Children.Count);

        foreach (var child in snapshot.Children)
        {
            PrintActorSnapshot(child, indent + "  ");
        }
    }
}

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
