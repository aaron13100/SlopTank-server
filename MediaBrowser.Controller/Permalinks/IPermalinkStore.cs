// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-08, 2026-09-09, 2026-10-01.
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Persists and validates durable permalink state owned outside metadata.
/// </summary>
public interface IPermalinkStore
{
    /// <summary>
    /// Takes the exclusive path lease and establishes the stable filesystem anchor (under a
    /// committed anchor-intent registry row) before content hashing.
    /// </summary>
    /// <param name="path">The current local content path.</param>
    /// <param name="itemKind">The immutable permalink item kind.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease. Dispose it after <see cref="EnsureAsync"/> settles or fails.</returns>
    Task<IPermalinkPathLease> BeginContentIdentityAsync(
        string path,
        string itemKind,
        CancellationToken cancellationToken);

    /// <summary>Ensures one verified fallback alias for the supplied snapshot.</summary>
    /// <param name="request">The immutable item snapshot.</param>
    /// <param name="lease">The lease from <see cref="BeginContentIdentityAsync"/> for the request path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded durable state.</returns>
    Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        IPermalinkPathLease lease,
        CancellationToken cancellationToken);
}
