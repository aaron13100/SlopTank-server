// SlopTank modification notice: added or changed by SlopTank on 2026-09-04, 2026-09-09.
using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The result of priming the journal's pending index.
///
/// This returns an object rather than the bare pending list because the caller
/// that primes the index is also the only caller positioned to report what the
/// priming cost: it runs once, on the startup path, before anything else can
/// have walked the journal.
/// </summary>
/// <param name="Pending">The operations that can still advance.</param>
/// <param name="Walk">
/// What the enumeration cost, or null when the index was already built and no
/// enumeration was needed.
/// </param>
internal sealed record PermalinkPendingIndexPriming(
    IReadOnlyList<Guid> Pending,
    PermalinkJournalWalk? Walk);
