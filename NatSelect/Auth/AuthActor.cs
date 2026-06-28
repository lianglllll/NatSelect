using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NatSelect.Common;
using NatSelect.Core;
using Serilog;

namespace NatSelect.Auth;

public class AuthActor : Actor
{
    private readonly ConcurrentDictionary<string, UserInfo> _users = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private long _nextUserId = 1;

    public AuthActor(ActorContext context) : base(context)
    {
        Log.Information("[AuthActor] Created at {Path}", Context.Path);
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case RegisterRequest req:
                HandleRegister(req);
                break;

            case LoginRequest req:
                HandleLogin(req);
                break;

            case LogoutRequest req:
                HandleLogout(req);
                break;

            case ValidateTokenRequest req:
                HandleValidateToken(req);
                break;

            default:
                Log.Warning("[AuthActor] Unknown message type: {Type}", msg.GetType().Name);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleRegister(RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username))
        {
            SendResponse(req.Sender, new RegisterResponse
            {
                Success = false,
                Message = "用户名不能为空"
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(req.Password))
        {
            SendResponse(req.Sender, new RegisterResponse
            {
                Success = false,
                Message = "密码不能为空"
            });
            return;
        }

        if (_users.ContainsKey(req.Username))
        {
            SendResponse(req.Sender, new RegisterResponse
            {
                Success = false,
                Message = "用户名已存在"
            });
            return;
        }

        var userId = Interlocked.Increment(ref _nextUserId);
        var user = new UserInfo
        {
            UserId = userId,
            Username = req.Username,
            PasswordHash = HashPassword(req.Password),
            Email = req.Email
        };

        if (_users.TryAdd(req.Username, user))
        {
            Log.Information("[AuthActor] User registered: {Username} (ID: {UserId})", req.Username, userId);
            SendResponse(req.Sender, new RegisterResponse
            {
                Success = true,
                Message = "注册成功",
                UserId = userId
            });
        }
        else
        {
            SendResponse(req.Sender, new RegisterResponse
            {
                Success = false,
                Message = "注册失败，用户名已被占用"
            });
        }
    }

    private void HandleLogin(LoginRequest req)
    {
        if (!_users.TryGetValue(req.Username, out var user))
        {
            SendResponse(req.Sender, new LoginResponse
            {
                Success = false,
                Message = "用户名或密码错误"
            });
            return;
        }

        if (!VerifyPassword(req.Password, user.PasswordHash))
        {
            SendResponse(req.Sender, new LoginResponse
            {
                Success = false,
                Message = "用户名或密码错误"
            });
            return;
        }

        var token = GenerateToken();
        var session = new Session
        {
            Token = token,
            UserId = user.UserId,
            Username = user.Username,
            CreatedAt = DateTime.UtcNow
        };

        _sessions[token] = session;

        Log.Information("[AuthActor] User logged in: {Username} (Token: {Token})", req.Username, token);

        SendResponse(req.Sender, new LoginResponse
        {
            Success = true,
            Message = "登录成功",
            Token = token,
            UserId = user.UserId
        });
    }

    private void HandleLogout(LogoutRequest req)
    {
        if (_sessions.TryRemove(req.Token, out var session))
        {
            Log.Information("[AuthActor] User logged out: {Username} (Token: {Token})", session.Username, req.Token);
            SendResponse(req.Sender, new LogoutResponse
            {
                Success = true,
                Message = "登出成功"
            });
        }
        else
        {
            SendResponse(req.Sender, new LogoutResponse
            {
                Success = false,
                Message = "无效的 Token"
            });
        }
    }

    private void HandleValidateToken(ValidateTokenRequest req)
    {
        if (_sessions.TryGetValue(req.Token, out var session))
        {
            SendResponse(req.Sender, new ValidateTokenResponse
            {
                Valid = true,
                UserId = session.UserId,
                Username = session.Username
            });
        }
        else
        {
            SendResponse(req.Sender, new ValidateTokenResponse
            {
                Valid = false,
                UserId = 0,
                Username = string.Empty
            });
        }
    }

    private void SendResponse(ActorRef target, IAMessage response)
    {
        if (target.IsValid)
        {
            _ = Context.SendAsync(target, response);
        }
    }

    private string HashPassword(string password)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
        return Convert.ToBase64String(bytes);
    }

    private bool VerifyPassword(string password, string hash)
    {
        return HashPassword(password) == hash;
    }

    private string GenerateToken()
    {
        var guid = Guid.NewGuid();
        return Convert.ToBase64String(guid.ToByteArray())
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private class UserInfo
    {
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
    }

    private class Session
    {
        public string Token { get; set; } = string.Empty;
        public long UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}