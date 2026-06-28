using NatSelect.Auth;
using NatSelect.Core;
using Serilog;

namespace NatSelectDemo;

public class AuthDemoActor : Actor
{
    private ActorRef _authRef;
    private string? _currentToken;

    public AuthDemoActor(ActorContext context, ActorRef authRef) : base(context)
    {
        _authRef = authRef;
        Log.Information("[AuthDemoActor] Created at {Path}", Context.Path);
    }

    protected override ValueTask OnReceiveAsync(IAMessage msg)
    {
        switch (msg)
        {
            case RegisterResponse resp:
                Log.Information("[AuthDemoActor] Register response: Success={Success}, Message={Message}, UserId={UserId}",
                    resp.Success, resp.Message, resp.UserId);
                if (resp.Success)
                {
                    _ = Context.SendAsync(_authRef, new LoginRequest
                    {
                        Username = "testuser",
                        Password = "testpass123"
                    });
                }
                break;

            case LoginResponse resp:
                Log.Information("[AuthDemoActor] Login response: Success={Success}, Message={Message}, Token={Token}",
                    resp.Success, resp.Message, resp.Token);
                if (resp.Success)
                {
                    _currentToken = resp.Token;
                    _ = Context.SendAsync(_authRef, new ValidateTokenRequest
                    {
                        Token = resp.Token
                    });
                }
                break;

            case ValidateTokenResponse resp:
                Log.Information("[AuthDemoActor] Validate token: Valid={Valid}, UserId={UserId}, Username={Username}",
                    resp.Valid, resp.UserId, resp.Username);
                if (resp.Valid && _currentToken != null)
                {
                    _ = Context.SendAsync(_authRef, new LogoutRequest
                    {
                        Token = _currentToken
                    });
                }
                break;

            case LogoutResponse resp:
                Log.Information("[AuthDemoActor] Logout response: Success={Success}, Message={Message}",
                    resp.Success, resp.Message);
                break;

            default:
                Log.Warning("[AuthDemoActor] Unknown message type: {Type}", msg.GetType().Name);
                break;
        }

        return ValueTask.CompletedTask;
    }
}