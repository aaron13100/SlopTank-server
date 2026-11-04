// SlopTank modification notice: added or changed by SlopTank on 2026-11-03, 2026-11-04.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.PlaybackPreparation;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.PlaybackPreparation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.PlaybackPreparation;

/// <summary>Runs the bundled preparation tool without cron, API keys or customer account credentials.</summary>
internal sealed class PlaybackPreparationHost : BackgroundService
{
    private readonly IPlaybackPreparationService _service;
    private readonly IApplicationPaths _paths;
    private readonly IMediaEncoder _encoder;
    private readonly IServerApplicationHost _appHost;
    private readonly ILibraryManager _library;
    private readonly ISessionManager _sessions;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaybackPreparationHost> _logger;
    private readonly string? _executable;
    private readonly string _retiredDirectory;

    public PlaybackPreparationHost(
        IPlaybackPreparationService service,
        IApplicationPaths paths,
        IMediaEncoder encoder,
        IServerApplicationHost appHost,
        ILibraryManager library,
        ISessionManager sessions,
        IConfiguration configuration,
        TimeProvider clock,
        ILogger<PlaybackPreparationHost> logger)
    {
        _service = service;
        _paths = paths;
        _encoder = encoder;
        _appHost = appHost;
        _library = library;
        _sessions = sessions;
        _clock = clock;
        _logger = logger;
        _executable = configuration["PlaybackPreparation:WorkerPath"];
        var retiredDirectory = configuration["PlaybackPreparation:RetiredDirectory"];
        _retiredDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(retiredDirectory)
            ? Path.Combine(paths.ProgramDataPath, "retired-media")
            : retiredDirectory);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_executable))
        {
            // Existing administrator-managed installs can retain their external
            // worker. Consumer launchers supply the bundled executable explicitly.
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_appHost.CoreStartupHasCompleted && IsIdle())
                {
                    var claim = await _service.ClaimAsync(stoppingToken).ConfigureAwait(false);
                    if (claim is not null)
                    {
                        await RunJobAsync(claim, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogDebug("Bundled playback preparation stopped with the server");
                return;
            }
            catch (PreparationProcessCleanupException exception)
            {
                _logger.LogCritical(exception, "Preparation worker cleanup failed; refusing to launch another worker");
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Bundled playback preparation failed; pending media will retry through its existing lease policy");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private bool IsIdle()
    {
        var oldest = _clock.GetUtcNow().UtcDateTime.AddMinutes(-3);
        return !_sessions.Sessions.Any(session => session.NowPlayingItem is not null
            && session.PlayState?.IsPaused != true
            && (session.LastPlaybackCheckIn == default || session.LastPlaybackCheckIn >= oldest));
    }

    private async Task RunJobAsync(PlaybackPreparationRequest claim, CancellationToken stoppingToken)
    {
        // A job has one process and one bounded lifetime. There is no second
        // worker scheduler or durable job database alongside the existing lease.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromHours(24));
        var token = deadline.Token;
        var workspace = Path.Combine(_paths.TempDirectory, "playback-preparation", Guid.Parse(claim.RequestId!).ToString("N"));
        Directory.CreateDirectory(workspace);
        var start = new ProcessStartInfo(_executable!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false, true),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            CreateNoWindow = true,
            WorkingDirectory = workspace
        };
        foreach (var key in start.Environment.Keys.Where(IsSecretEnvironment).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["TMPDIR"] = workspace;
        start.Environment["TMP"] = workspace;
        start.Environment["TEMP"] = workspace;

        using var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("preparation-worker-start-failed: the bundled preparation process did not start.");
        }

        process.StandardInput.NewLine = "\n";
        var stderr = DrainErrorsAsync(process.StandardError, claim.RequestId!, CancellationToken.None);
        var writer = new PreparationMediaWriter(_service, _library, _appHost, _retiredDirectory, IsIdle, claim, workspace);
        var channel = new PreparationWorkerChannel(_service, writer, claim, IsIdle, _logger);
        Exception? primaryFailure = null;
        try
        {
            await channel.RunAsync(
                process.StandardOutput,
                process.StandardInput,
                new
                {
                    jobWorkspace = workspace,
                    profileRoot = _paths.ProgramDataPath,
                    mediaRoot = Path.GetDirectoryName(claim.Path!),
                    ffmpegPath = _encoder.EncoderPath,
                    ffprobePath = _encoder.ProbePath
                },
                token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new IOException($"preparation-worker-exit: request '{claim.RequestId}' exited with code {process.ExitCode}.");
            }

            _logger.LogInformation("Bundled preparation completed for item {ItemId}, request {RequestId}", claim.ItemId, claim.RequestId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            // Never leave an encoder behind when its server or pipe disappears.
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited)
                    {
                        _logger.LogDebug(exception, "Preparation worker {RequestId} exited while termination was being requested", claim.RequestId);
                    }
                }

                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                await stderr.WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                var cause = primaryFailure is null ? exception
                    : new AggregateException("Preparation and child cleanup both failed.", primaryFailure, exception);
                throw new PreparationProcessCleanupException(claim.RequestId!, cause);
            }
        }

        // The durable writer owns any recovery files. This directory contains
        // only disposable child scratch, and is removed after a successful exit.
        Directory.Delete(workspace, recursive: true);
    }

    private async Task DrainErrorsAsync(StreamReader reader, string requestId, CancellationToken token)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            _logger.LogDebug("Preparation worker {RequestId}: {Diagnostic}", requestId, new string(buffer, 0, count));
        }
    }

    private static bool IsSecretEnvironment(string name) => name.StartsWith("JELLYFIN_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SLOPTANK_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("E2E_", StringComparison.OrdinalIgnoreCase)
        || new[] { "TOKEN", "PASSWORD", "PASSWD", "SECRET", "API_KEY", "APIKEY", "AUTHORIZATION", "CREDENTIAL" }
            .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private sealed class PreparationProcessCleanupException(string requestId, Exception cause)
        : Exception($"preparation-worker-cleanup-failed: request '{requestId}' could not be reaped ({cause.Message}).", cause)
    {
    }
}
