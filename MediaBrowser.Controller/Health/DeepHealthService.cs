// SlopTank modification notice: added or changed by SlopTank on 2026-09-07, 2026-09-08, 2026-09-09.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.System;

namespace MediaBrowser.Controller.Health;

/// <summary>
/// Evaluates cheap subsystem probes without starting processes or scanning media.
/// </summary>
public sealed class DeepHealthService : IDeepHealthService
{
    /// <summary>
    /// A transcode that has reported no progress for this long is stalled.
    /// Five minutes avoids treating cold external-volume startup as a failure.
    /// </summary>
    public static readonly TimeSpan TranscodeStallThreshold = TimeSpan.FromMinutes(5);

    private readonly IDeepHealthProbe _probe;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeepHealthService"/> class.
    /// </summary>
    /// <param name="probe">The narrow production probe boundary.</param>
    public DeepHealthService(IDeepHealthProbe probe)
    {
        _probe = probe;
    }

    /// <inheritdoc />
    public async Task<DeepHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var checks = new List<DeepHealthCheck>(4)
        {
            await CheckDatabaseAsync(cancellationToken).ConfigureAwait(false),
            CheckLibraryRoots(),
            CheckFfmpeg(),
            CheckTranscodeProgress(now)
        };

        return new DeepHealthReport
        {
            CheckedAtUtc = now,
            State = AggregateState(checks),
            Checks = checks
        };
    }

    private async Task<DeepHealthCheck> CheckDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _probe.QueryDatabaseAsync(cancellationToken).ConfigureAwait(false);
            return Result("database", "ok", "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Result("database", "unavailable", "internal_error");
        }
    }

    private DeepHealthCheck CheckLibraryRoots()
    {
        try
        {
            var roots = _probe.GetLibraryRoots()
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var affected = 0;
            foreach (var path in roots)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        affected++;
                        continue;
                    }

                    // MoveNext performs the required live list operation but
                    // stops after the first entry. Empty readable roots pass.
                    using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    _ = entries.MoveNext();
                }
                catch (IOException)
                {
                    affected++;
                }
                catch (UnauthorizedAccessException)
                {
                    affected++;
                }
            }

            return affected == 0
                ? Result("library_roots", "ok", "ok", roots.Length, 0)
                : Result("library_roots", "degraded", "unreachable", roots.Length, affected);
        }
        catch
        {
            return Result("library_roots", "unknown", "internal_error");
        }
    }

    private DeepHealthCheck CheckFfmpeg()
    {
        try
        {
            var path = _probe.GetFfmpegPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Result("ffmpeg", "degraded", "unreachable");
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode executable = UnixFileMode.UserExecute
                                                | UnixFileMode.GroupExecute
                                                | UnixFileMode.OtherExecute;
                if ((mode & executable) == 0)
                {
                    return Result("ffmpeg", "degraded", "not_executable");
                }
            }

            return Result("ffmpeg", "ok", "ok");
        }
        catch (IOException)
        {
            return Result("ffmpeg", "degraded", "unreachable");
        }
        catch (UnauthorizedAccessException)
        {
            return Result("ffmpeg", "degraded", "not_executable");
        }
        catch
        {
            return Result("ffmpeg", "unknown", "internal_error");
        }
    }

    private DeepHealthCheck CheckTranscodeProgress(DateTime now)
    {
        try
        {
            return _probe.HasStalledTranscode(now - TranscodeStallThreshold)
                ? Result("transcode_progress", "degraded", "stalled")
                : Result("transcode_progress", "ok", "ok");
        }
        catch
        {
            return Result("transcode_progress", "unknown", "internal_error");
        }
    }

    private static DeepHealthCheck Result(
        string name,
        string state,
        string code,
        int? observedCount = null,
        int? affectedCount = null)
        => new()
        {
            Name = name,
            State = state,
            Code = code,
            ObservedCount = observedCount,
            AffectedCount = affectedCount
        };

    private static string AggregateState(IEnumerable<DeepHealthCheck> checks)
    {
        var states = checks.Select(check => check.State).ToHashSet(StringComparer.Ordinal);
        if (states.Contains("unavailable"))
        {
            return "unavailable";
        }

        if (states.Contains("degraded"))
        {
            return "degraded";
        }

        return states.Contains("unknown") ? "unknown" : "ok";
    }
}
