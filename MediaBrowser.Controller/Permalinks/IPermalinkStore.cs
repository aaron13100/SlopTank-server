using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// Persists and validates durable permalink state owned outside metadata.
/// </summary>
public interface IPermalinkStore
{
    /// <summary>
    /// Validates and establishes the stable filesystem anchor before content hashing.
    /// </summary>
    /// <param name="path">The current local content path.</param>
    /// <param name="itemKind">The immutable permalink item kind.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PrepareContentIdentityAsync(
        string path,
        string itemKind,
        CancellationToken cancellationToken);

    /// <summary>Ensures one verified fallback alias for the supplied snapshot.</summary>
    /// <param name="request">The immutable item snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folded durable state.</returns>
    Task<PermalinkStoredState> EnsureAsync(
        PermalinkStoreRequest request,
        CancellationToken cancellationToken);
}
