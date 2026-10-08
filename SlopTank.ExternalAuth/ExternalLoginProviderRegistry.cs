// SlopTank modification notice: added or changed by SlopTank on 2026-10-08.

namespace SlopTank.ExternalAuth;

/// <summary>Lifecycle status of a registered provider instance.</summary>
public enum ExternalLoginProviderStatus
{
    /// <summary>Active; eligible for discovery and new flows.</summary>
    Enabled = 0,

    /// <summary>Registered but not active; discovery omits it and new flows
    /// against it are refused with a typed code.</summary>
    Disabled = 1,

    /// <summary>Terminal: the instance was removed. Its id and epoch must
    /// never be treated as active again.</summary>
    Retired = 2,
}

/// <summary>
/// Immutable descriptor of a provider instance. The epoch is the plugin's
/// monotonic configuration generation for this instance: every registration
/// change (create, update, disable, enable) must carry a strictly higher
/// epoch, so a disabled or retired instance and epoch can never be mistaken
/// for a later active one, and in-flight records bound to an old epoch are
/// detectably stale.
/// </summary>
public sealed record ExternalLoginProviderDescriptor(
    string Id,
    string DisplayName,
    int Order,
    long Epoch,
    ExternalLoginProviderStatus Status);

/// <summary>
/// A provider registration pair: the immutable descriptor plus the provider
/// instance serving it. The provider instance itself must be immutable for
/// the lifetime of the registration; configuration changes register a new
/// instance under a higher epoch.
/// </summary>
public sealed record ExternalLoginProviderRegistration(
    ExternalLoginProviderDescriptor Descriptor,
    IExternalLoginProvider Provider);

/// <summary>Outcome of a registration lifecycle call.</summary>
public enum ExternalLoginRegisterStatus
{
    /// <summary>The descriptor is now the live registration.</summary>
    Registered = 0,

    /// <summary>The descriptor carries an epoch that is not strictly newer
    /// than the live epoch (and not an identical idempotent
    /// re-registration); nothing changed.</summary>
    StaleEpoch = 1,

    /// <summary>No live registration exists under this id.</summary>
    UnknownProvider = 2,
}

/// <summary>Typed result of a registration lifecycle call.</summary>
public sealed record ExternalLoginRegisterResult
{
    private ExternalLoginRegisterResult(ExternalLoginRegisterStatus status, long liveEpoch)
    {
        Status = status;
        LiveEpoch = liveEpoch;
    }

    /// <summary>Gets the lifecycle outcome.</summary>
    public ExternalLoginRegisterStatus Status { get; }

    /// <summary>Gets the live epoch after the call; zero when no live
    /// registration exists.</summary>
    public long LiveEpoch { get; }

    /// <summary>Builds a successful registration result.</summary>
    /// <param name="epoch">The now-live epoch.</param>
    /// <returns>The result.</returns>
    public static ExternalLoginRegisterResult Registered(long epoch) =>
        new(ExternalLoginRegisterStatus.Registered, epoch);

    /// <summary>Builds a stale-epoch refusal.</summary>
    /// <param name="liveEpoch">The current live epoch.</param>
    /// <returns>The result.</returns>
    public static ExternalLoginRegisterResult StaleEpoch(long liveEpoch) =>
        new(ExternalLoginRegisterStatus.StaleEpoch, liveEpoch);

    /// <summary>Builds an unknown-provider refusal.</summary>
    /// <returns>The result.</returns>
    public static ExternalLoginRegisterResult UnknownProvider() =>
        new(ExternalLoginRegisterStatus.UnknownProvider, 0);
}

/// <summary>
/// The provider contract every external-login plugin implements. This is the
/// generic, provider-neutral core seam: no OIDC or vendor type may appear
/// here, and implementations must be usable from any host that registers the
/// seam services.
/// </summary>
public interface IExternalLoginProvider
{
    /// <summary>Gets the stable opaque id of this provider instance (a slug
    /// assigned by the plugin's configuration; never a vendor name).</summary>
    string Id { get; }

    /// <summary>
    /// Creates an authorization challenge for a new flow. Implementations
    /// must await their real preparation work and honor the cancellation
    /// token; flow failures come back as typed error results, never as
    /// escaping exceptions.
    /// </summary>
    /// <param name="context">The flow context including the one-use
    /// correlation secret.</param>
    /// <param name="cancellationToken">Cancels the challenge
    /// preparation.</param>
    /// <returns>The redirect URL or a typed failure.</returns>
    Task<ExternalLoginChallengeResult> CreateChallengeAsync(
        ExternalLoginContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runtime registry of provider instances with an explicit registration
/// lifecycle for dynamic provider CRUD. The host registers this as a
/// singleton; plugins mutate it when their configuration changes. Readers
/// always observe an immutable snapshot.
/// </summary>
public interface IExternalLoginProviderRegistry
{
    /// <summary>Registers or replaces a provider instance. Refuses a
    /// descriptor whose epoch is not strictly newer than the live epoch for
    /// the same id, unless it is byte-identical to the live registration
    /// (idempotent re-registration).</summary>
    /// <param name="descriptor">The new immutable descriptor.</param>
    /// <param name="provider">The immutable provider instance.</param>
    /// <returns>The typed registration outcome.</returns>
    ExternalLoginRegisterResult Register(
        ExternalLoginProviderDescriptor descriptor,
        IExternalLoginProvider provider);

    /// <summary>Retires (removes) a provider instance. Requires the exact
    /// live epoch; a stale epoch is refused so a concurrent reload cannot
    /// retire a newer configuration.</summary>
    /// <param name="providerId">The instance id.</param>
    /// <param name="epoch">The epoch the caller believes is live.</param>
    /// <returns>The typed retirement outcome.</returns>
    ExternalLoginRegisterResult Retire(string providerId, long epoch);

    /// <summary>Retires every registration whose provider instance was
    /// created by the given assembly. Used when a plugin is disabled or
    /// unloaded so none of its instances stay active.</summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <returns>The number of instances retired.</returns>
    int RetireAll(System.Reflection.Assembly assembly);

    /// <summary>Gets an immutable snapshot of every live descriptor,
    /// including disabled ones, ordered by <see cref="ExternalLoginProviderDescriptor.Order"/>
    /// then id.</summary>
    /// <returns>The snapshot.</returns>
    IReadOnlyList<ExternalLoginProviderDescriptor> GetDescriptors();

    /// <summary>Gets an immutable snapshot of the active (enabled,
    /// current-epoch) registrations, ordered for discovery.</summary>
    /// <returns>The snapshot.</returns>
    IReadOnlyList<ExternalLoginProviderRegistration> GetActiveProviders();

    /// <summary>Resolves the active registration for a provider id.
    /// Disabled, retired or stale registrations are not active.</summary>
    /// <param name="providerId">The instance id.</param>
    /// <returns>The active registration, or null.</returns>
    ExternalLoginProviderRegistration? GetActive(string providerId);
}

/// <summary>Thread-safe default <see cref="IExternalLoginProviderRegistry"/>.
/// Retired ids are remembered forever: a later registration under the same
/// id is allowed only with a strictly newer epoch, and retirement records
/// are never resurrected.</summary>
public sealed class ExternalLoginProviderRegistry : IExternalLoginProviderRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ExternalLoginProviderRegistration> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _retiredHighWaterEpoch = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ExternalLoginRegisterResult Register(
        ExternalLoginProviderDescriptor descriptor,
        IExternalLoginProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(descriptor.Id)
            || !string.Equals(descriptor.Id, provider.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The descriptor id must be non-empty and match the provider instance id.");
        }

        if (descriptor.Epoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), "Epoch must be at least 1.");
        }

        lock (_sync)
        {
            if (_retiredHighWaterEpoch.TryGetValue(descriptor.Id, out var retiredEpoch)
                && descriptor.Epoch <= retiredEpoch)
            {
                return ExternalLoginRegisterResult.StaleEpoch(retiredEpoch);
            }

            if (_live.TryGetValue(descriptor.Id, out var current)
                && descriptor.Epoch < current.Descriptor.Epoch)
            {
                return ExternalLoginRegisterResult.StaleEpoch(current.Descriptor.Epoch);
            }

            if (current is not null
                && descriptor.Epoch == current.Descriptor.Epoch
                && descriptor == current.Descriptor
                && ReferenceEquals(provider, current.Provider))
            {
                return ExternalLoginRegisterResult.Registered(current.Descriptor.Epoch);
            }

            _live[descriptor.Id] = new ExternalLoginProviderRegistration(descriptor, provider);
            return ExternalLoginRegisterResult.Registered(descriptor.Epoch);
        }
    }

    /// <inheritdoc />
    public ExternalLoginRegisterResult Retire(string providerId, long epoch)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        lock (_sync)
        {
            if (!_live.TryGetValue(providerId, out var current))
            {
                return ExternalLoginRegisterResult.UnknownProvider();
            }

            if (epoch != current.Descriptor.Epoch)
            {
                return ExternalLoginRegisterResult.StaleEpoch(current.Descriptor.Epoch);
            }

            _live.Remove(providerId);
            _retiredHighWaterEpoch[providerId] = epoch;
            return ExternalLoginRegisterResult.Registered(epoch);
        }
    }

    /// <inheritdoc />
    public int RetireAll(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (_sync)
        {
            var doomed = _live.Where(kv => ReferenceEquals(kv.Value.Provider.GetType().Assembly, assembly)).ToList();
            foreach (var kv in doomed)
            {
                _live.Remove(kv.Key);
                _retiredHighWaterEpoch[kv.Key] = kv.Value.Descriptor.Epoch;
            }

            return doomed.Count;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExternalLoginProviderDescriptor> GetDescriptors()
    {
        lock (_sync)
        {
            return _live.Values
                .Select(static r => r.Descriptor)
                .OrderBy(static d => d.Order)
                .ThenBy(static d => d.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExternalLoginProviderRegistration> GetActiveProviders()
    {
        lock (_sync)
        {
            return _live.Values
                .Where(static r => r.Descriptor.Status == ExternalLoginProviderStatus.Enabled)
                .OrderBy(static r => r.Descriptor.Order)
                .ThenBy(static r => r.Descriptor.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <inheritdoc />
    public ExternalLoginProviderRegistration? GetActive(string providerId)
    {
        ArgumentNullException.ThrowIfNull(providerId);
        lock (_sync)
        {
            return _live.TryGetValue(providerId, out var current)
                && current.Descriptor.Status == ExternalLoginProviderStatus.Enabled
                ? current
                : null;
        }
    }
}
