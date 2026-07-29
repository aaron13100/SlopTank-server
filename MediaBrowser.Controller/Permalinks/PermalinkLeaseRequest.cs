namespace MediaBrowser.Controller.Permalinks;

/// <summary>Opaque lease body accepted by candidate redemption.</summary>
/// <param name="Lease">The purpose-bound lease.</param>
public sealed record PermalinkLeaseRequest(string Lease);
