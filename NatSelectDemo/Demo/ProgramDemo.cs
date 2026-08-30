using NatSelect.Auth;
using NatSelect.Core;
using Serilog;

namespace NatSelectDemo.Demo;

/// <summary>
/// 引擎演示程序
/// 展示 Actor 创建、消息通信、定时器、诊断等核心能力
/// </summary>
public static class ProgramDemo
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

        // 5. 演示用户认证功能
        await RunAuthDemoAsync(engine, rootContext);

        // 6. 演示协程原语（挂起不占 Worker，恢复经大队列调度）
        await RunCoroutineDemoAsync(engine, rootContext, echo1Ref);

        // 7. 打印诊断信息
        PrintDiagnostics(engine);

        Log.Information("========== Demo Complete ==========");
    }

    private static async Task RunAuthDemoAsync(NatSelectEngine engine, ActorContext rootContext)
    {
        Log.Information("========== Auth Demo Starting ==========");

        var authRef = engine.ActorSystem.SpawnActor<AuthActor>(rootContext, "auth");
        Log.Information("Created AuthActor: {Ref}", authRef);

        var demoRef = engine.ActorSystem.SpawnActor<AuthDemoActor>(rootContext, "auth-demo", authRef);
        Log.Information("Created AuthDemoActor: {Ref}", demoRef);

        await engine.ActorSystem.SendAsync(authRef, new RegisterRequest
        {
            Sender = demoRef,
            Username = "testuser",
            Password = "testpass123",
            Email = "test@example.com"
        });

        await Task.Delay(1000);

        Log.Information("========== Auth Demo Complete ==========");
    }

    private static async Task RunCoroutineDemoAsync(NatSelectEngine engine, ActorContext rootContext, ActorRef echoRef)
    {
        Log.Information("========== Coroutine Demo Starting ==========");

        // 创建 20 个协程 Actor（超过 Worker 数，验证挂起不阻塞调度）
        var flowRefs = new List<ActorRef>();
        for (int i = 0; i < 20; i++)
        {
            flowRefs.Add(engine.ActorSystem.SpawnActor<CoroutineDemoActor>(rootContext, $"flow-{i}"));
        }

        foreach (var flowRef in flowRefs)
        {
            await rootContext.SendAsync(flowRef, new RunFlow());
        }

        // 挂起期间 EchoActor 应正常处理（旧模型下 16 个挂起即停摆）
        await Task.Delay(200);
        for (int i = 1; i <= 3; i++)
        {
            await engine.ActorSystem.SendAsync(echoRef, new PingMessage
            {
                Text = $"during-suspension #{i}"
            });
            await Task.Delay(200);
        }

        // 等待所有协程流程完成
        await Task.Delay(2500);

        Log.Information("========== Coroutine Demo Complete ==========");
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
        Log.Information("{Indent}[{State}] {Name} @ {Path} (children: {ChildCount})",
            indent, snapshot.State, snapshot.Name, snapshot.Path, snapshot.Children.Count);

        foreach (var child in snapshot.Children)
        {
            PrintActorSnapshot(child, indent + "  ");
        }
    }
}
