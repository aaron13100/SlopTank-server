// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using SourceKind = Emby.Server.Implementations.Permalinks.PermalinkEvidenceRegistryStore.PermalinkPopulationSourceKind;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Builds the complete physical-source plan from freshly attested configured roots.</summary>
internal sealed class PermalinkEvidencePopulationPlan
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly PermalinkEvidenceRoots _evidenceRoots;
    private readonly PermalinkRootIdentityRegistry _rootRegistry;

    /// <summary>Initializes a new instance of the <see cref="PermalinkEvidencePopulationPlan"/> class.</summary>
    /// <param name="authority">The private authority and operation-journal root.</param>
    /// <param name="evidenceRoots">The configured external recovery roots.</param>
    /// <param name="rootRegistry">The root marker and physical identity verifier.</param>
    public PermalinkEvidencePopulationPlan(
        PermalinkAuthorityStore authority,
        PermalinkEvidenceRoots evidenceRoots,
        PermalinkRootIdentityRegistry rootRegistry)
    {
        _authority = authority;
        _evidenceRoots = evidenceRoots;
        _rootRegistry = rootRegistry;
    }

    /// <summary>Attests every source root and returns the complete deterministic plan.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The exact physical source requests.</returns>
    public async Task<IReadOnlyCollection<PermalinkEvidenceSourceCensus.Request>> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var authorityRoot = await _rootRegistry.EnsureKnownAsync(_authority.Root, cancellationToken)
            .ConfigureAwait(false);
        var authorityPermalinksRoot = Path.Combine(
            authorityRoot.CanonicalRoot,
            ".sloptank",
            "permalinks");
        var requests = new List<PermalinkEvidenceSourceCensus.Request>
        {
            new(
                SourceKind.JournalHot,
                authorityRoot.RootIdentityId,
                authorityRoot.CanonicalRoot,
                Path.Combine(authorityPermalinksRoot, "operations"),
                "journal-hot"),
            new(
                SourceKind.JournalArchive,
                authorityRoot.RootIdentityId,
                authorityRoot.CanonicalRoot,
                Path.Combine(authorityPermalinksRoot, "operations-archive"),
                "journal-archive")
        };

        var seenEvidenceRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuredRoot in _evidenceRoots.ConfiguredRoots)
        {
            var identity = await _rootRegistry.EnsureKnownAsync(configuredRoot, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(identity.RootIdentityId, authorityRoot.RootIdentityId, StringComparison.Ordinal))
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "permalink-root-role-overlap",
                    $"Evidence root '{identity.CanonicalRoot}' is also the private authority root.");
            }

            if (!seenEvidenceRoots.Add(identity.RootIdentityId))
            {
                continue;
            }

            requests.Add(new PermalinkEvidenceSourceCensus.Request(
                SourceKind.EvidenceRoot,
                identity.RootIdentityId,
                identity.CanonicalRoot,
                identity.CanonicalRoot,
                "evidence:" + identity.RootIdentityId));
        }

        return requests;
    }
}
