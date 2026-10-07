// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>An opaque process-local identity for one selected authority scope.</summary>
internal readonly record struct PermalinkAuthorityNamespace
{
    private readonly string _value;

    /// <summary>Initializes a new instance of the <see cref="PermalinkAuthorityNamespace"/> struct.</summary>
    /// <param name="value">The authority path and reset generation encoded by the authority store.</param>
    internal PermalinkAuthorityNamespace(string value)
    {
        _value = value;
    }
}
