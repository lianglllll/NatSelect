using Serilog;

namespace NatSelect.Config;

/// <summary>
/// 配置管理器
/// 提供配置热加载和变更通知机制
/// </summary>
public sealed class ConfigManager<T> : IAsyncDisposable where T : class, new()
{
    private readonly string _filePath;
    private T _currentConfig;
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private int _debounceMs;

    /// <summary>
    /// 配置变更事件
    /// </summary>
    public event Action<T>? OnConfigChanged;

    /// <summary>
    /// 当前配置
    /// </summary>
    public T Current => _currentConfig;

    public ConfigManager(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _currentConfig = ConfigLoader.Load<T>(filePath);
    }

    /// <summary>
    /// 手动触发配置重载
    /// </summary>
    /// <returns>新配置（如果解析成功）</returns>
    public T Reload()
    {
        lock (_reloadLock)
        {
            try
            {
                var newConfig = ConfigLoader.Load<T>(_filePath);
                var oldConfig = _currentConfig;
                _currentConfig = newConfig;

                Log.Information("[ConfigManager] Configuration reloaded from {Path}", _filePath);

                // 触发变更事件
                OnConfigChanged?.Invoke(newConfig);

                return newConfig;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ConfigManager] Failed to reload configuration, keeping old config");
                return _currentConfig;
            }
        }
    }

    /// <summary>
    /// 启用文件变更监听
    /// </summary>
    /// <param name="debounceMs">防抖间隔（毫秒），防止短时间内多次触发</param>
    public void EnableFileWatcher(int debounceMs = 500)
    {
        _debounceMs = debounceMs;

        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        var fileName = Path.GetFileName(_filePath);

        if (string.IsNullOrEmpty(directory))
        {
            Log.Warning("[ConfigManager] Cannot enable file watcher: invalid path {Path}", _filePath);
            return;
        }

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        DateTime lastChange = DateTime.MinValue;

        _watcher.Changed += (_, e) =>
        {
            // 防抖
            if ((DateTime.Now - lastChange).TotalMilliseconds < _debounceMs)
                return;

            lastChange = DateTime.Now;

            Log.Information("[ConfigManager] Config file changed, reloading...");

            // 延迟一小段时间再重载，确保文件写入完成
            Task.Delay(100).ContinueWith(_ => Reload());
        };

        Log.Information("[ConfigManager] File watcher enabled for {Path}", _filePath);
    }

    public ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        return ValueTask.CompletedTask;
    }
}
