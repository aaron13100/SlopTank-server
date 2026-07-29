using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: covered by private HTTP suite sloptank-tests/server/tests/Jellyfin.Server.Integration.Tests/Controllers/PermalinkResolutionControllerTests.cs

/// <summary>
/// Reclaims terminal or expired playback state once after each server start.
/// </summary>
internal sealed class PermalinkPlaybackRecovery : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PermalinkAuthorityStore _authority;
    private readonly ILogger<PermalinkPlaybackRecovery> _logger;
    private readonly TimeProvider _timeProvider;
    private bool _recovered;

    public PermalinkPlaybackRecovery(
        PermalinkAuthorityStore authority,
        ILogger<PermalinkPlaybackRecovery> logger,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (_recovered)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recovered)
            {
                return;
            }

            var root = Path.Combine(
                _authority.Root,
                ".sloptank",
                "permalink-playback-leases");
            if (Directory.Exists(root))
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasValidTerminalMarker(directory, "completed.json", "created_at")
                        || HasValidTerminalMarker(
                            directory,
                            "consumed.json",
                            "playback_session_id")
                        || await IsExpiredAsync(directory, cancellationToken).ConfigureAwait(false))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            }

            _recovered = true;
        }
        catch (IOException exception)
        {
            throw Unavailable(exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Unavailable(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<bool> IsExpiredAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        var leasePath = Path.Combine(directory, "lease.json");
        if (!File.Exists(leasePath))
        {
            return false;
        }

        try
        {
            var document = CanonicalJson.Deserialize<PermalinkLeaseDocument>(
                await File.ReadAllBytesAsync(leasePath, cancellationToken).ConfigureAwait(false),
                leasePath);
            return DateTimeOffset.Parse(
                document.ExpiresAt,
                CultureInfo.InvariantCulture) <= _timeProvider.GetUtcNow();
        }
        catch (PermalinkException exception) when (exception.Code == "malformed-document")
        {
            _logger.LogWarning(
                exception,
                "Retaining malformed playback lease during cleanup: {LeasePath}",
                leasePath);
            return false;
        }
        catch (FormatException exception)
        {
            _logger.LogWarning(
                exception,
                "Retaining playback lease with invalid expiration during cleanup: {LeasePath}",
                leasePath);
            return false;
        }
    }

    private bool HasValidTerminalMarker(
        string directory,
        string fileName,
        string requiredProperty)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(requiredProperty, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString());
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Retaining malformed playback terminal state during cleanup: {StatePath}",
                path);
            return false;
        }
    }

    private static PermalinkException Unavailable(Exception exception)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "playback-cleanup-io",
            $"Playback state cleanup failed ({exception.Message}).",
            exception);
    }
}
