// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.
// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkReplacementCoherenceTests.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Keeps the library's media view on the physical generation a sanctioned permalink
/// mutation just installed. Both mutation seams that publish a new generation (the
/// media swap at a constant path, and the path bundle that repoints the item onto a
/// new file) must re-probe through the same provider the library uses for freshly
/// created rows, or the persisted stream rows and every served media view keep
/// describing the superseded generation until some later scan happens to re-probe
/// it (publication acceptance runs 7-8: a Ready, visible item served pre-swap
/// streams with <c>SupportsDirectPlay=false</c>).
/// </summary>
internal sealed class PermalinkAdoptedGenerationProber
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ProbeProvider _probeProvider;
    private readonly ILogger<PermalinkAdoptedGenerationProber> _logger;

    public PermalinkAdoptedGenerationProber(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ProbeProvider probeProvider,
        ILogger<PermalinkAdoptedGenerationProber> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _probeProvider = probeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Re-probes the item's media info from the generation just installed at its path.
    /// The destination bytes are already installed, so a primary Movie/Episode first
    /// re-enters durable Pending admission before the probe. A successful probe leaves
    /// it Complete but Pending until the new generation is attested; a failed probe
    /// leaves it ProbeFailed and hidden while retaining the old stream rows only for
    /// diagnosis. The next library refresh retries the failed generation.
    /// </summary>
    /// <param name="item">The item whose current path holds the adopted generation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous re-probe, which never fails the
    /// surrounding commit: a probe failure is logged and deferred to the next refresh.</returns>
    public async Task ReprobeAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Video video || !ProbeProvider.Supports(item))
        {
            return;
        }

        var requiresReadinessAttestation = item is Movie or Episode;
        if (requiresReadinessAttestation)
        {
            item.IngestState = IngestState.Pending;
            item.PlaybackReadiness = PlaybackReadiness.Pending;
            item.PlaybackReadyObjectIdentity = null;
            item.PlaybackReadyAlgorithmVersion = null;
            await PersistStateAsync(item, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem));
            await _probeProvider.FetchVideoInfo(video, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Probing replaced media at {Path} failed after the generation commit; the item keeps its previously probed streams and the next library refresh retries",
                item.Path);
        }

        if (requiresReadinessAttestation)
        {
            await PersistStateAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-registers the persisted row so the GetItemById-cached instance (the seam
    /// PlaybackInfo and the visible-item paths resolve) reflects the committed
    /// generation's row, independent of which instance the mutation mutated.
    /// </summary>
    /// <param name="itemId">The item whose persisted row changed.</param>
    public void RefreshCachedItem(Guid itemId)
    {
        if (_libraryManager.RetrieveItem(itemId) is { } committed)
        {
            _libraryManager.RegisterItem(committed);
        }
    }

    private async Task PersistStateAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var parent = item.GetParent()
            ?? throw new InvalidOperationException("A replaced library item has no parent for readiness persistence.");
        await _libraryManager.UpdateItemAsync(
            item,
            parent,
            ItemUpdateType.MetadataEdit,
            cancellationToken).ConfigureAwait(false);
        RefreshCachedItem(item.Id);
    }
}
