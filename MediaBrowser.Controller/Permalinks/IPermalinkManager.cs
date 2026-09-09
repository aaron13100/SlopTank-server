// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-09.
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Issues and validates durable permalink aliases for visible library items.
/// </summary>
public interface IPermalinkManager
{
    /// <summary>
    /// Ensures the item has a currently resolvable ordered alias set.
    /// </summary>
    /// <param name="item">The local library item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The versioned permalink response.</returns>
    Task<PermalinkResponse> EnsurePermalinkIdsAsync(
        BaseItem item,
        CancellationToken cancellationToken);
}
