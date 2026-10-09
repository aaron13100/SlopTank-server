// SlopTank modification notice: added or changed by SlopTank on 2026-10-08, 2026-10-09.

namespace SlopTank.ExternalAuth;

/// <summary>
/// Core-level sanitized error codes for the external-login seam. These are
/// the only failure reasons the core controller and correlation services
/// surface; provider plugins own their own flow-level vocabularies and map
/// them onto these codes or carry them in their own channels. The enum is
/// additive-only from version 1: never remove, repurpose or reorder the
/// meaning of an existing value.
/// </summary>
public enum ExternalAuthErrorCode
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>No provider is registered under the requested id.</summary>
    ProviderUnknown = 10,

    /// <summary>The provider instance is registered but not enabled; it must
    /// not be treated as active.</summary>
    ProviderDisabled = 11,

    /// <summary>The provider instance or its configuration epoch is retired
    /// or stale; the flow must not proceed against it.</summary>
    ProviderEpochStale = 12,

    /// <summary>The provider failed to produce a challenge. Detail, when
    /// present, is a sanitized short token with no credential content.</summary>
    ChallengeFailed = 20,

    /// <summary>The server-side pending-flow store is at its hard bound and
    /// no expired entries could be reclaimed; the begin request is
    /// refused.</summary>
    TooManyPendingFlows = 30,

    /// <summary>The request transport is not HTTPS and the development HTTP
    /// profile is not explicitly active, so correlation cookies cannot be
    /// minted.</summary>
    TransportInsecure = 31,

    /// <summary>The callback did not carry the per-flow correlation cookie,
    /// or its value does not match the pending flow.</summary>
    BrowserBindingFailed = 40,

    /// <summary>The pending flow reference is unknown (never existed or
    /// already consumed).</summary>
    FlowUnknown = 41,

    /// <summary>The pending flow expired before the callback arrived.</summary>
    FlowExpired = 42,

    /// <summary>The request presented no authenticated local user session
    /// usable to initiate a link, or the presented credential was rejected;
    /// an explicit-but-rejected credential never falls back to another
    /// transport.</summary>
    LinkSessionInvalid = 50,

    /// <summary>The recorded originating session of a link flow failed
    /// re-resolution (revoked, replaced, disabled, mismatched, or of external
    /// origin).</summary>
    LinkSessionNoLongerValid = 51,

    /// <summary>The validated identity conflicts with a durable link
    /// already settled (the identity is linked to another local user, or
    /// the user already has a different active link).</summary>
    LinkConflict = 52,

    /// <summary>The durable link authority refused the settlement (state
    /// root unavailable, descriptor policy, corruption, or a failed atomic
    /// publication); the link is NOT confirmed.</summary>
    LinkPersistenceFailed = 53,

    /// <summary>The provider's account-safety policy excludes the resolved
    /// local user from external linking and session minting (for example a
    /// designated emergency local administrator).</summary>
    AccountExcluded = 54,

    /// <summary>The provider completed authentication but no healthy local
    /// account and session could be resolved or minted for the browser.</summary>
    SessionUnavailable = 60,
}
