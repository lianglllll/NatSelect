using NatSelect.Core;
using Serilog;

namespace NatSelect;

public class Program
{
    static async Task Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : "Config/config.yaml";

        await using var engine = new NatSelectEngine(configPath);

        // 注册 Ctrl+C 优雅退出
        var shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += async (_, e) =>
        {
            e.Cancel = true;
            Log.Information("Ctrl+C detected, shutting down...");
            await engine.StopAsync();
            shutdownCts.Cancel();
        };

        try
        {
            await engine.StartAsync();

            // ========== Demo: 演示 Actor 系统 ==========
            await RunDemo(engine);

            Log.Information("Demo completed. Press Ctrl+C to exit.");

            // 等待关闭信号
            await Task.Delay(Timeout.Infinite, shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal error!");
        }

        Log.Information("NatSelect Engine shutdown complete.");
    }

    static async Task RunDemo(NatSelectEngine engine)
    {
        Log.Information("========== NatSelect Demo Starting ==========");

        // 1. 创建一个 RootActor 作为所有 Actor 的父节点
        // 由于 LocalActorSystem.SpawnActor 需要 parent，我们用引擎的 ActorSystem 创建一个根上下文
        var rootContext = new ActorContext(engine.ActorSystem, new ActorRef(engine.NodeId, 999999), null, "/");

        // 2. 创建两个 EchoActor
        var echo1Ref = engine.ActorSystem.SpawnActor<EchoActor>(rootContext, "echo-1");
        var echo2Ref = engine.ActorSystem.SpawnActor<EchoActor>(rootContext, "echo-2");

        Log.Information("Created EchoActor 1: {Ref}", echo1Ref);
        Log.Information("Created EchoActor 2: {Ref}", echo2Ref);

        // 3. 向 echo-1 发送几条消息
        for (int i = 1; i <= 5; i++)
        {
            await engine.ActorSystem.SendAsync(echo1Ref, new PingMessage
            {
                Sender = echo2Ref, // echo2 发送
                Text = $"Hello from demo #{i}"
            });

            await Task.Delay(100); // 稍微延迟，让消息有时间处理
        }

        // 4. 等待消息处理完成
        await Task.Delay(500);

        // 5. 打印诊断信息
        Log.Information("========== Actor System Diagnostics ==========");
        Log.Information("Total Actors: {Count}", engine.ActorSystem.GetTotalActorCount());

        if (engine.ActorSystem is LocalActorSystem localSystem)
        {
            var tree = localSystem.GetActorTree();
            Log.Information("Actor Tree ({Count} roots):", tree.Count);
            foreach (var info in tree)
            {
                PrintActorInfo(info, "");
            }
        }

        Log.Information("========== Demo Complete ==========");
        Log.Information("(Timers will keep ticking every 3 seconds. Press Ctrl+C to stop.)");
    }

    static void PrintActorInfo(ActorInfo info, string indent)
    {
        Log.Information("{Indent}[{State}] {Name} @ {Path} (children: {ChildCount})",
            indent, info.State, info.Name, info.Path, info.Children.Count);

        foreach (var child in info.Children)
        {
            PrintActorInfo(child, indent + "  ");
        }
    }
}
