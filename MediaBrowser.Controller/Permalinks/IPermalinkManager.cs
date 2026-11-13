// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-09-09, 2026-10-14, 2026-10-31, 2026-11-04, 2026-11-13.
using System;
using System.Collections.Generic;
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

    /// <summary>Resolves an alias through current library metadata and ordinary user authorization.</summary>
    /// <param name="identifier">The public alias.</param>
    /// <param name="userId">The authenticated library user.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One visible current record per matching logical title.</returns>
    Task<IReadOnlyList<BaseItem>> GetVideoFileAsync(string identifier, Guid userId, CancellationToken cancellationToken);

    /// <summary>Reads removal provenance for an administrator without changing the binding.</summary>
    /// <param name="identifier">An existing public title alias.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The indexing/removal state, or null when the alias is unknown.</returns>
    Task<PermalinkRemovalResponse?> GetRemovalAsync(string identifier, CancellationToken cancellationToken);

    /// <summary>Records an administrator's confirmation of an already-absent title's deliberate removal.</summary>
    /// <param name="identifier">An existing public title alias.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The recorded state, or null when the alias is unknown.</returns>
    Task<PermalinkRemovalResponse?> ConfirmRemovalAsync(string identifier, CancellationToken cancellationToken);
}
