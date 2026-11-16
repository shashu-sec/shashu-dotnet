namespace Shashu;

/// <summary>
/// 401 — authentication failure. When the gateway names a cloud-pass refusal,
/// <see cref="Code"/> is one of the six <c>pass_*</c> codes and the message
/// is the gateway's sentence, unchanged.
/// </summary>
public sealed class ShashuAuthenticationException : ShashuException
{
    /// <summary>One of the six <c>pass_*</c> codes, or null for other 401s.</summary>
    public string? Code { get; }

    internal ShashuAuthenticationException(int statusCode, string responseBody, string message, string? code)
        : base(statusCode, responseBody, message)
    {
        Code = code;
    }
}
