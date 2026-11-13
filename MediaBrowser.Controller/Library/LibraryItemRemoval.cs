// SlopTank modification notice: added or changed by SlopTank on 2026-11-13.
using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.Library;

/// <summary>Requests one transactional library removal with its known origin.</summary>
/// <param name="ItemIds">The library records and their owned descendants to remove.</param>
/// <param name="Reason">The observed origin, rather than an inference from a missing file.</param>
public sealed record LibraryItemRemoval(IReadOnlyList<Guid> ItemIds, LibraryRemovalReason Reason);
