using NatSelect.Core;
using NatSelect.Network;

namespace NatSelect.Auth;

[ProtoId(1001)]
public class LoginRequest : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

[ProtoId(1002)]
public class LoginResponse : IAMessage
{
    public ActorRef Sender { get; set; }
    public bool Success { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long UserId { get; set; }
}

[ProtoId(1003)]
public class RegisterRequest : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

[ProtoId(1004)]
public class RegisterResponse : IAMessage
{
    public ActorRef Sender { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public long UserId { get; set; }
}

[ProtoId(1005)]
public class LogoutRequest : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Token { get; set; } = string.Empty;
}

[ProtoId(1006)]
public class LogoutResponse : IAMessage
{
    public ActorRef Sender { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

[ProtoId(1007)]
public class ValidateTokenRequest : IAMessage
{
    public ActorRef Sender { get; set; }
    public string Token { get; set; } = string.Empty;
}

[ProtoId(1008)]
public class ValidateTokenResponse : IAMessage
{
    public ActorRef Sender { get; set; }
    public bool Valid { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = string.Empty;
}