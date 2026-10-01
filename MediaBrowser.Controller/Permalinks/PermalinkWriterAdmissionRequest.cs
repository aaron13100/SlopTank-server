// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
namespace MediaBrowser.Controller.Permalinks;

/// <summary>Requests a short, single-use filesystem writer admission.</summary>
public sealed record PermalinkWriterAdmissionRequest(
    string RootIdentityId,
    string NormalizedRelativePath,
    string PinnedObjectIdentity,
    string Action,
    long SnapshotGeneration);
