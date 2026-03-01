using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NatSelect.Core;

/// <summary>
/// 所有Actor消息的基接口（强制Sender标记）
/// </summary>
public interface IAMessage
{
    ActorRef Sender { get; set; }
}

/// <summary>
/// 系统保留消息标记（调度器特殊处理）
/// </summary>
public interface ISystemMessage : IAMessage { }

/// <summary>
/// 需要响应的消息标记（错误反馈时使用）
/// </summary>
public interface IResponseRequired : IAMessage { }

/// <summary>
/// 业务异常（玩家操作错误，不触发Actor停止）
/// </summary>
public class BusinessException : Exception
{
    public int Code { get; }
    public BusinessException(int code, string message) : base(message) => Code = code;
}
