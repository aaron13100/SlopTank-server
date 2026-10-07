// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Supplies the permalink authority root so the authority store can resolve it
/// through a seam instead of caching it in a constructor field.
///
/// The production implementation reads
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> exactly once
/// per provider lifetime, which preserves the value and lifetime the store's
/// own constructor field had. Because the store now resolves through this
/// seam on every use, a test host can swap the supplied value and reset the
/// store onto a fresh authority root without rebuilding the server.
/// </summary>
public interface IPermalinkAuthorityRootProvider
{
    /// <summary>
    /// Gets the configured authority root, or null when permalink authority is
    /// not configured.
    /// </summary>
    string? AuthorityRoot { get; }
}
