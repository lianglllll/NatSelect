using NatSelect.Core;
using Serilog;
using Serilog.Events;

namespace NatSelect.Logger;


public static class NSLogger
{
    private static ILogger _logger = null!;
    private static readonly object _initLock = new();

    /// <summary>
    /// 初始化（程序启动时调用一次）
    /// </summary>
    public static void Initialize(string serviceName, string logDirectory)
    {
        lock (_initLock)
        {
            if (_logger != null) return;

            // 启动时间戳，用于日志文件名
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            _logger = new LoggerConfiguration()
                // ===== 全局上下文注入 =====
                .Enrich.WithProperty("Service", serviceName)
                .Enrich.WithProperty("Machine", Environment.MachineName)
                .Enrich.WithThreadId() // 线程ID（排查线程问题）

                // ===== 控制台输出（开发环境）=====
                .WriteTo.Async(a => a.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Debug))

                // ===== 文件输出（生产环境核心）=====
                .WriteTo.Async(a => a.File(
                    Path.Combine(logDirectory, $"game-{timestamp}.log"),
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30, // 保留30天
                    fileSizeLimitBytes: 100_000_000, // 单文件100MB
                    restrictedToMinimumLevel: LogEventLevel.Information))

                // ===== 错误日志单独文件（运维刚需）=====
                .WriteTo.Async(a => a.File(
                    Path.Combine(logDirectory, $"errors-{timestamp}.log"),
                    restrictedToMinimumLevel: LogEventLevel.Error,
                    rollingInterval: RollingInterval.Day))

                // ===== 关键：结构化JSON输出（对接ELK）=====
                .WriteTo.Async(a => a.File(
                    path: Path.Combine(logDirectory, $"structured-{timestamp}.json"),
                    formatter: new Serilog.Formatting.Json.JsonFormatter(),
                    rollingInterval: RollingInterval.Day,
                    restrictedToMinimumLevel: LogEventLevel.Information,
                    fileSizeLimitBytes: 100_000_000,
                    retainedFileCountLimit: 30
                ))

                // ===== 性能保护 =====
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .CreateLogger();

            Log.Logger = _logger; // 设置全局静态Logger（兼容第三方库）
            _logger.Information("FrameworkLogger initialized for {Service}", serviceName);
        }
    }

    /// <summary>
    /// 从ActorContext获取带上下文的日志器（关键！）
    /// </summary>
    public static ILogger ForContext(ActorContext context)
    {
        return _logger
            .ForContext("ActorPath", context.Path) // 自动注入Actor路径
            .ForContext("ActorId", context.Self.ActorId);
        // 可扩展：.ForContext("RoomId", GetRoomId(context))
    }

    /// <summary>
    /// 全局关闭（程序退出时调用）
    /// </summary>
    public static void Shutdown() => Log.CloseAndFlush();
}
