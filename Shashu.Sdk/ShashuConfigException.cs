namespace Shashu;

/// <summary>
/// Thrown when no gateway address resolves — neither an explicit option nor
/// the <c>SHASHU_BASE_URL</c> environment variable.
/// </summary>
/// <remarks>
/// <para>The SDK has no default address and will not guess one. Shashu is
/// self-hosted: a vendor-hostname fallback would send a misconfigured
/// process's prompts to a host nobody in that organisation chose, which is a
/// data-egress incident rather than a convenience.</para>
///
/// <para>The <c>Exception</c> suffix is .NET's convention; the other SDKs name
/// the same type <c>ShashuEnvError</c>. <c>contracts/sdk-surface.v1.md</c>
/// specifies "a <c>ShashuEnvError</c> equivalent" and allows each
/// language its own idiom for the name.</para>
///
/// <para>It subclasses <see cref="ShashuException"/> so a caller with one
/// <c>catch (ShashuException)</c> around their gateway calls still catches
/// it; <see cref="ShashuException.StatusCode"/> is 0 and
/// <see cref="ShashuException.ResponseBody"/> empty, because no request was
/// ever made.</para>
/// </remarks>
public sealed class ShashuConfigException : ShashuException
{
    /// <summary>Construct with the frozen, hostname-free message.</summary>
    public ShashuConfigException(string message) : base(message) { }
}
