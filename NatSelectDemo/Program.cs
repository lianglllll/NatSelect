using System.Threading;
using NatSelect.Core;
using NatSelectDemo;
using Serilog;

namespace NatSelectDemo;

public class Program
{
    // 命名事件名称，stop.bat 通过此名称触发关闭
    private const string ShutdownEventName = "NatSelect_Shutdown";

    static async Task Main(string[] args)
    {
        // 配置文件查找：依次尝试多个位置
        string configPath = args.Length > 0 ? args[0] : FindConfig();

        await using var engine = new NatSelectEngine(configPath);

        // ===== 关闭信号机制 =====
        var shutdownCts = new CancellationTokenSource();

        // 1. 创建命名事件（供 stop.bat 触发）
        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName);

        // 2. Ctrl+C 也能优雅退出
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Log.Information("Ctrl+C detected, shutting down...");
            shutdownCts.Cancel();
        };

        // 3. 后台线程监听命名事件
        _ = Task.Run(async () =>
        {
            shutdownEvent.WaitOne();
            Log.Information("Shutdown signal received from stop.bat");
            shutdownCts.Cancel();
        });

        try
        {
            await engine.StartAsync();

            // 运行演示
            await ProgramDemo.RunAsync(engine);

            Log.Information("Use stop.bat or Ctrl+C to exit.");

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

        // 优雅关闭引擎
        await engine.StopAsync();
        Log.Information("NatSelect Engine shutdown complete.");
    }

    /// <summary>
    /// 查找配置文件（兼容多种运行方式的工作目录）
    /// </summary>
    static string FindConfig()
    {
        string[] candidates =
        [
            "Config/config.yaml",                    // 从 Demo 项目目录运行
            "NatSelect/Config/config.yaml",           // 从解决方案根目录运行 (dotnet run --project)
            "../NatSelect/Config/config.yaml",        // 从 Demo 项目目录向上查找
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        // 都找不到，使用默认值（引擎会用内置默认配置）
        return "Config/config.yaml";
    }
}
