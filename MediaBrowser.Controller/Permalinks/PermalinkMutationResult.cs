using System;

namespace MediaBrowser.Controller.Permalinks;

/// <summary>Public folded state of a durable mutation operation.</summary>
/// <param name="OperationId">The durable operation identifier.</param>
/// <param name="State">The current folded state.</param>
public sealed record PermalinkMutationResult(Guid OperationId, string State);
