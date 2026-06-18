using System.Net;
using System.Net.Sockets;
using Serilog;

namespace NatSelect.Network;

/// <summary>
/// TCP 服务端监听器
/// 负责接受客户端连接，并将 Socket 传递给上层处理
/// </summary>
public sealed class TcpServerListener : IAsyncDisposable
{
    private Socket? _listenSocket;
    private bool _isStopping;
    private int _maxConnections;
    private int _currentConnectionCount;

    // 回调
    private Action<Socket>? _onClientAccepted;

    /// <summary>
    /// 启动监听
    /// </summary>
    /// <param name="port">监听端口</param>
    /// <param name="maxConnections">最大连接数</param>
    /// <param name="onClientAccepted">新连接回调</param>
    public async Task StartAsync(int port, int maxConnections, Action<Socket> onClientAccepted)
    {
        _maxConnections = maxConnections;
        _onClientAccepted = onClientAccepted;
        _isStopping = false;

        _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listenSocket.Bind(new IPEndPoint(IPAddress.Any, port));
        _listenSocket.Listen(maxConnections);

        Log.Information("[TcpServerListener] Listening on port {Port} (max: {Max})", port, maxConnections);

        // 异步接受连接循环
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_isStopping && _listenSocket != null)
        {
            try
            {
                var clientSocket = await _listenSocket.AcceptAsync();

                // 检查连接数限制
                if (Interlocked.Increment(ref _currentConnectionCount) > _maxConnections)
                {
                    Log.Warning("[TcpServerListener] Connection limit reached ({Max}), rejecting client", _maxConnections);
                    Interlocked.Decrement(ref _currentConnectionCount);
                    clientSocket.Close();
                    continue;
                }

                Log.Debug("[TcpServerListener] Client connected from {Remote}", clientSocket.RemoteEndPoint);
                _onClientAccepted?.Invoke(clientSocket);
            }
            catch (ObjectDisposedException)
            {
                // 监听 Socket 被关闭，正常退出
                break;
            }
            catch (SocketException ex) when (_isStopping)
            {
                // 正在关闭，忽略
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[TcpServerListener] Error accepting connection");
                await Task.Delay(100); // 避免快速重试
            }
        }

        Log.Information("[TcpServerListener] Accept loop stopped");
    }

    /// <summary>
    /// 连接断开时调用（由 TcpConnection 调用）
    /// </summary>
    public void OnConnectionClosed()
    {
        Interlocked.Decrement(ref _currentConnectionCount);
    }

    /// <summary>
    /// 当前活跃连接数
    /// </summary>
    public int CurrentConnectionCount => _currentConnectionCount;

    /// <summary>
    /// 停止监听
    /// </summary>
    public Task StopAsync()
    {
        _isStopping = true;

        if (_listenSocket != null)
        {
            try
            {
                _listenSocket.Close();
                _listenSocket.Dispose();
            }
            catch { }
            _listenSocket = null;
        }

        Log.Information("[TcpServerListener] Stopped (connections remaining: {Count})", _currentConnectionCount);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
