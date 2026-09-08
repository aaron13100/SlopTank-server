using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private PermalinkPlaybackRetentionTests.cs

/// <summary>
/// Performs opt-in, bounded reclamation of dead permalink playback leases.
/// </summary>
internal sealed class PermalinkPlaybackRecovery : IDisposable
{
    /// <summary>Gets the deployment gate for playback-lease retention.</summary>
    public const string EnabledKey = "Permalinks:PlaybackLeaseRetentionEnabled";

    /// <summary>Gets the maximum number of direct lease-root entries examined per pass.</summary>
    public const string MaximumEntriesPerPassKey = "Permalinks:PlaybackLeaseRetentionMaximumEntriesPerPass";

    /// <summary>Gets the minimum delay between request-triggered retention passes.</summary>
    public const string MinimumIntervalKey = "Permalinks:PlaybackLeaseRetentionMinimumInterval";

    /// <summary>Gets the age required before inactive terminal state can be reclaimed.</summary>
    public const string TerminalRetentionKey = "Permalinks:PlaybackLeaseTerminalRetention";

    private const int AbsoluteMaximumEntriesPerPass = 1024;
    private static readonly TimeSpan _absoluteMaximumInterval = TimeSpan.FromDays(365);
    private static readonly TimeSpan _absoluteMaximumRetention = TimeSpan.FromDays(3650);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _admissionLocks = new();
    private readonly PermalinkAuthorityStore _authority;
    private readonly ILogger<PermalinkPlaybackRecovery> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly int _maximumEntriesPerPass;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeSpan _terminalRetention;
    private IEnumerator<string>? _entries;
    private DateTimeOffset _nextPassAt = DateTimeOffset.MinValue;

    public PermalinkPlaybackRecovery(
        PermalinkAuthorityStore authority,
        IConfiguration configuration,
        ILogger<PermalinkPlaybackRecovery> logger,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _logger = logger;
        _timeProvider = timeProvider;
        _enabled = configuration.GetValue(EnabledKey, false);
        _maximumEntriesPerPass = configuration.GetValue(MaximumEntriesPerPassKey, 32);
        _minimumInterval = configuration.GetValue(MinimumIntervalKey, TimeSpan.FromMinutes(5));
        _terminalRetention = configuration.GetValue(TerminalRetentionKey, TimeSpan.FromHours(24));

        if (_maximumEntriesPerPass <= 0
            || _maximumEntriesPerPass > AbsoluteMaximumEntriesPerPass)
        {
            throw InvalidConfiguration(
                MaximumEntriesPerPassKey,
                $"must be between 1 and {AbsoluteMaximumEntriesPerPass}");
        }

        if (_minimumInterval < TimeSpan.Zero || _minimumInterval > _absoluteMaximumInterval)
        {
            throw InvalidConfiguration(MinimumIntervalKey, "must be between zero and 365 days");
        }

        if (_terminalRetention < TimeSpan.Zero || _terminalRetention > _absoluteMaximumRetention)
        {
            throw InvalidConfiguration(TerminalRetentionKey, "must be between zero and 3650 days");
        }
    }

    /// <summary>
    /// Examines at most one configured batch and reclaims only positively dead entries.
    /// </summary>
    /// <param name="isActive">Checks the in-process active-session registry while admission is locked.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounded pass result.</returns>
    public async Task<PlaybackLeaseReclamationResult> ReclaimAsync(
        Func<string, bool> isActive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isActive);
        if (!_enabled || !_authority.IsConfigured)
        {
            return PlaybackLeaseReclamationResult.Disabled(_maximumEntriesPerPass);
        }

        var now = _timeProvider.GetUtcNow();
        if (now < _nextPassAt)
        {
            return PlaybackLeaseReclamationResult.Noop(_maximumEntriesPerPass);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (now < _nextPassAt)
            {
                return PlaybackLeaseReclamationResult.Noop(_maximumEntriesPerPass);
            }

            var startedAt = Stopwatch.GetTimestamp();
            var result = await ReclaimPassAsync(
                now,
                isActive,
                cancellationToken).ConfigureAwait(false);
            _nextPassAt = now + _minimumInterval;
            _logger.LogInformation(
                "Playback lease retention examined {Examined}/{Maximum} entries in {ElapsedMs:F1} ms: "
                + "{Reclaimed} reclaimed, {Active} active, {Retained} retained, {Refused} refused, "
                + "{Failed} failed.",
                result.Examined,
                result.MaximumEntriesPerPass,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                result.Reclaimed,
                result.Active,
                result.Retained,
                result.Refused,
                result.Failed);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ResetEnumeration();
        foreach (var admissionLock in _admissionLocks.Values)
        {
            admissionLock.Dispose();
        }

        _gate.Dispose();
    }

    internal SemaphoreSlim GetAdmissionLock(string handle)
    {
        return _admissionLocks.GetOrAdd(handle, _ => new SemaphoreSlim(1, 1));
    }

    private async Task<PlaybackLeaseReclamationResult> ReclaimPassAsync(
        DateTimeOffset now,
        Func<string, bool> isActive,
        CancellationToken cancellationToken)
    {
        var result = new PlaybackLeaseReclamationAccumulator(_maximumEntriesPerPass);
        var root = Path.Combine(
            _authority.Root,
            ".sloptank",
            "permalink-playback-leases");
        if (PermalinkPlaybackLeaseShape.IsLink(root))
        {
            ResetEnumeration();
            result.RootRefused = true;
            _logger.LogError(
                "Refusing playback lease reclamation because the lease root is a symbolic link: {Root}",
                root);
            return result.Freeze();
        }

        if (!Directory.Exists(root))
        {
            ResetEnumeration();
            return result.Freeze();
        }

        try
        {
            _entries ??= Directory.EnumerateFileSystemEntries(root).GetEnumerator();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ResetEnumeration();
            result.Failed++;
            _logger.LogError(
                exception,
                "Playback lease enumeration could not start; no lease state was changed.");
            return result.Freeze();
        }

        while (result.Examined < _maximumEntriesPerPass)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entry;
            try
            {
                if (!_entries.MoveNext())
                {
                    ResetEnumeration();
                    break;
                }

                entry = _entries.Current;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ResetEnumeration();
                result.Failed++;
                _logger.LogError(
                    exception,
                    "Playback lease enumeration failed closed; no unexamined state was changed.");
                break;
            }

            // The cap counts every direct filesystem entry before any validation or deletion.
            result.Examined++;
            var handle = Path.GetFileName(entry);
            if (!IsConfinedDirectDirectory(root, entry) || string.IsNullOrWhiteSpace(handle))
            {
                result.Refused++;
                continue;
            }

            var admissionLock = GetAdmissionLock(handle);
            await admissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check the path after acquiring the shared admission lock. Admission inserts
                // the active-session record before releasing this same lock, closing the race
                // between a stale map check and bounded state deletion.
                if (PermalinkPlaybackLeaseShape.IsLink(root))
                {
                    ResetEnumeration();
                    result.RootRefused = true;
                    result.Refused++;
                    _logger.LogError(
                        "Playback lease root changed to a symbolic link during reclamation: {Root}",
                        root);
                    break;
                }

                if (!IsConfinedDirectDirectory(root, entry))
                {
                    result.Refused++;
                }
                else if (isActive(handle))
                {
                    result.Active++;
                }
                else
                {
                    ReclaimInactive(entry, now, result);
                }
            }
            finally
            {
                admissionLock.Release();
            }
        }

        return result.Freeze();
    }

    private void ReclaimInactive(
        string directory,
        DateTimeOffset now,
        PlaybackLeaseReclamationAccumulator result)
    {
        try
        {
            var consumed = ReadTerminalMarker(directory, "consumed.json", "playback_session_id", now);
            var completed = ReadTerminalMarker(directory, "completed.json", "playback_session_id", now);
            if (consumed == TerminalMarker.Invalid || completed == TerminalMarker.Invalid)
            {
                result.Refused++;
                return;
            }

            if (consumed == TerminalMarker.Retained || completed == TerminalMarker.Retained)
            {
                result.Retained++;
                return;
            }

            if (consumed == TerminalMarker.Reclaimable || completed == TerminalMarker.Reclaimable)
            {
                ReclaimKnownShape(directory, result);
                return;
            }

            var leasePath = Path.Combine(directory, "lease.json");
            if (!File.Exists(leasePath))
            {
                result.Refused++;
                return;
            }

            PermalinkLeaseDocument lease;
            try
            {
                lease = CanonicalJson.Deserialize<PermalinkLeaseDocument>(
                    File.ReadAllBytes(leasePath),
                    leasePath);
            }
            catch (PermalinkException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Retaining malformed playback lease during reclamation: {LeasePath}",
                    leasePath);
                result.Refused++;
                return;
            }

            if (!DateTimeOffset.TryParse(
                    lease.ExpiresAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var expiresAt))
            {
                result.Refused++;
                return;
            }

            if (expiresAt <= now)
            {
                ReclaimKnownShape(directory, result);
            }
            else
            {
                result.Retained++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result.Failed++;
            _logger.LogWarning(
                exception,
                "Playback lease reclamation failed closed; retaining {LeaseDirectory}.",
                directory);
        }
    }

    private TerminalMarker ReadTerminalMarker(
        string directory,
        string fileName,
        string requiredProperty,
        DateTimeOffset now)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return TerminalMarker.Missing;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(requiredProperty, out var required)
                || required.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(required.GetString())
                || !document.RootElement.TryGetProperty("created_at", out var created)
                || created.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(
                    created.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var createdAt))
            {
                return TerminalMarker.Invalid;
            }

            return createdAt <= now - _terminalRetention
                ? TerminalMarker.Reclaimable
                : TerminalMarker.Retained;
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Retaining malformed playback terminal state during reclamation: {StatePath}",
                path);
            return TerminalMarker.Invalid;
        }
    }

    private static bool IsConfinedDirectDirectory(string root, string entry)
    {
        if (!string.Equals(
                Path.GetFullPath(Path.GetDirectoryName(entry)!),
                Path.GetFullPath(root),
                StringComparison.Ordinal)
            || PermalinkPlaybackLeaseShape.IsLink(entry))
        {
            return false;
        }

        return Directory.Exists(entry);
    }

    private static void ReclaimKnownShape(
        string directory,
        PlaybackLeaseReclamationAccumulator result)
    {
        if (!PermalinkPlaybackLeaseShape.TryDelete(directory))
        {
            result.Refused++;
            return;
        }

        result.Reclaimed++;
    }

    private void ResetEnumeration()
    {
        _entries?.Dispose();
        _entries = null;
    }

    private static PermalinkException InvalidConfiguration(string key, string constraint)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "playback-lease-retention-invalid",
            $"{key} {constraint}.");
    }

    private enum TerminalMarker
    {
        Missing,
        Retained,
        Reclaimable,
        Invalid
    }

}
