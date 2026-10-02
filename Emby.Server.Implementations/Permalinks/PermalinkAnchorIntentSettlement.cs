// SlopTank modification notice: added or changed by SlopTank on 2026-10-02.
using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The single way an unsettled anchor intent is closed: compare-and-remove the lease's own xattr,
/// then mark the intent row abandoned. A lease dispose, a failed xattr write and startup crash
/// recovery all call this, so they cannot drift apart on what "abandon" means.
/// </summary>
internal sealed class PermalinkAnchorIntentSettlement
{
    private readonly PermalinkPathEvidenceStore _evidence;
    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly IPermalinkPathObjects _objects;

    public PermalinkAnchorIntentSettlement(
        PermalinkPathEvidenceStore evidence,
        IPermalinkAtomicFileSystem fileSystem,
        IPermalinkPathObjects objects)
    {
        _evidence = evidence;
        _fileSystem = fileSystem;
        _objects = objects;
    }

    /// <summary>
    /// Removes the xattr at <paramref name="path"/> only when it still equals the intent's token
    /// (a missing file or an absent or different xattr is left alone), then marks the row abandoned.
    /// </summary>
    /// <param name="intent">The anchor-intent registry row id.</param>
    /// <param name="path">The absolute content path the intent covers.</param>
    /// <param name="anchorToken">The token the intent's lease wrote.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the anchor was found equal and removed from disk.</returns>
    public async Task<bool> AbandonAsync(
        Guid intent,
        string path,
        string anchorToken,
        CancellationToken cancellationToken)
    {
        var removed = _objects.ReadPinnedIdentityNoFollow(path) is not null
            && _fileSystem.RemoveAnchorTokenIfEquals(path, anchorToken);
        await _evidence.SetIntentStateAsync(
            intent,
            PermalinkPathEvidenceStore.IntentAbandoned,
            cancellationToken).ConfigureAwait(false);
        return removed;
    }
}
