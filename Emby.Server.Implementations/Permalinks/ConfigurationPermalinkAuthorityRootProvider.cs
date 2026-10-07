// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Resolves the permalink authority root from server configuration, reading it
/// exactly once per provider lifetime.
///
/// The authority store used to read <c>configuration["Permalinks:AuthorityRoot"]</c>
/// into its own constructor field; this provider carries that behavior so the
/// store can resolve the root through the seam instead while production
/// semantics stay unchanged: same value, read once, stable for the provider's
/// lifetime, and blind to later configuration mutations.
/// </summary>
public sealed class ConfigurationPermalinkAuthorityRootProvider : IPermalinkAuthorityRootProvider
{
    private readonly string? _authorityRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationPermalinkAuthorityRootProvider"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    public ConfigurationPermalinkAuthorityRootProvider(IConfiguration configuration)
    {
        _authorityRoot = configuration["Permalinks:AuthorityRoot"];
    }

    /// <inheritdoc />
    public string? AuthorityRoot => _authorityRoot;
}
