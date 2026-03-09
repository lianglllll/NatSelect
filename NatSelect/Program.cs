using NatSelect.Config;
using NatSelect.Config.Template;
using Serilog;

namespace NatSelect;

public class Program
{
    static void Main(string[] args)
    {
        // 1. 初始化日志
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            string configPath = args.Length > 0 ? args[0] : "config.yaml";

            // 2. 加载核心配置 (强类型)
            var settings = ConfigLoader.Load<ConfigTemplate>("config.yaml");

            // 3. 处理节点名称 -> ID 转换
            //string nodeName = settings.Node.Name; // "ob_game_1"
            //ulong nodeId = NodeRegistry.GetId(nodeName); // 转换为数字，例如 182374...

            Log.Information("=================================");
            Log.Information("Server Starting...");
            //Log.Information("Node Name: {Name}", nodeName);
            //Log.Information("Node ID  : {Id} (Internal)", nodeId);
            Log.Information("Port     : {Port}", settings.Network.ListenPort);
            Log.Information("Workers  : {Threads}", settings.Node.ActorSystem.WorkerThreads);
            Log.Information("=================================");

            // 4. 演示动态访问 (读取 game_balance)
            // 假设你想单独加载一个经常变的数值文件，或者直接从 settings.GameBalance 访问
            if (settings.GameBalance != null)
            {
                // 因为 GameBalance 定义为 object，我们需要把它转回 dynamic 或者 Dictionary
                // 这里为了演示，我们直接重新加载一次动态版，或者你在定义时直接用 dynamic
                var dynamicConfig = ConfigLoader.LoadDynamic("config.yaml");

                double expRate = dynamicConfig.game_balance.exp_multiplier;
                Log.Information("Game Balance - Exp Rate: {Rate}", expRate);

                // 访问列表
                foreach (var evt in dynamicConfig.game_balance.special_events)
                {
                    Log.Information("Active Event: {Event}", evt);
                }
            }

            // 5. 初始化你的 Actor System
            // var system = new ActorSystem(nodeId, settings.Node.ActorSystem);

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Server startup failed!");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
