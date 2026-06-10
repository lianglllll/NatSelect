namespace NatSelect.Common;

/// <summary>
/// 业务异常（玩家操作错误，不触发Actor停止）
/// </summary>
public class BusinessException : Exception
{
    public int Code { get; }
    public BusinessException(int code, string message) : base(message) => Code = code;
}
