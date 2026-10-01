// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>
/// An exclusive in-process claim on one content path, keyed by
/// <c>(RootIdentityId, NormalizedRelativePath)</c>, held from before the stable anchor is read
/// or created until the permalink ensure settles (P6 decisions, "Lease key" and "Interface").
/// Disposing a lease that created the anchor and never settled removes that anchor again.
/// </summary>
public interface IPermalinkPathLease : IAsyncDisposable
{
    /// <summary>Gets the registered content-root identity the key is relative to.</summary>
    string RootIdentityId { get; }

    /// <summary>Gets the NFC-normalized, <c>/</c>-separated path relative to the canonical root.</summary>
    string NormalizedRelativePath { get; }

    /// <summary>
    /// Gets the device and inode of the final path component from a no-follow lstat
    /// (<c>device:inode</c>), or <c>virtual</c> for a virtual season directory.
    /// </summary>
    string PinnedObjectIdentity { get; }

    /// <summary>Gets the stable anchor token read from, or created on, the path.</summary>
    string AnchorToken { get; }

    /// <summary>Gets a value indicating whether this lease created the anchor.</summary>
    bool CreatedAnchor { get; }

    /// <summary>Gets the highest reservation generation recorded for the key at acquisition.</summary>
    long ReservationGeneration { get; }

    /// <summary>Gets the authority content epoch at acquisition, kept for diagnostics only.</summary>
    long ContentEpochAtAcquire { get; }
}
