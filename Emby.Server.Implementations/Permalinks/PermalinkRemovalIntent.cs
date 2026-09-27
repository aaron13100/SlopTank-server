// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: serialized and recovered by private PermalinkRemovalControllerTests.cs

/// <summary>Immutable removal-only state frozen into a version-three operation document.</summary>
internal sealed record PermalinkRemovalIntent(
    Guid SourceCapsuleId,
    string SourceContentRoot,
    Guid? SurvivorItemId,
    Guid? SurvivorCapsuleId,
    string? SurvivorContentRoot,
    string SurvivorPath,
    string SourceAnchorToken,
    int SourceDevice,
    ulong SourceInode,
    long SourceLength,
    bool AttachedAtDestination,
    IReadOnlyList<PermalinkRemovalCompanion> Companions,
    IReadOnlyDictionary<string, string> SourceProviderIdentity,
    IReadOnlyList<string> SourceAliases,
    PermalinkRemovalEquivalenceEvidence EquivalenceEvidence);
