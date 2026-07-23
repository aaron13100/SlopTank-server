using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Transcoding throttler.
/// </summary>
public class TranscodingThrottler : IDisposable
{
    private readonly TranscodingJob _job;
    private readonly ILogger<TranscodingThrottler> _logger;
    private readonly IConfigurationManager _config;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaEncoder _mediaEncoder;
    private Timer? _timer;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodingThrottler"/> class.
    /// </summary>
    /// <param name="job">Transcoding job dto.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TranscodingThrottler}"/> interface.</param>
    /// <param name="config">Instance of the <see cref="IConfigurationManager"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    public TranscodingThrottler(TranscodingJob job, ILogger<TranscodingThrottler> logger, IConfigurationManager config, IFileSystem fileSystem, IMediaEncoder mediaEncoder)
    {
        _job = job;
        _logger = logger;
        _config = config;
        _fileSystem = fileSystem;
        _mediaEncoder = mediaEncoder;
    }

    /// <summary>
    /// Gets a value indicating whether the transcoding process is currently paused by this throttler.
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <summary>
    /// Start timer.
    /// </summary>
    public void Start()
    {
        _timer = new Timer(TimerCallback, null, 5000, 5000);
    }

    /// <summary>
    /// Unpause transcoding.
    /// </summary>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task UnpauseTranscoding()
    {
        if (IsPaused)
        {
            _logger.LogDebug("Sending resume command to ffmpeg");

            try
            {
                var resumeKey = _mediaEncoder.IsPkeyPauseSupported ? "u" : Environment.NewLine;
                await SendProcessInputAsync(resumeKey).ConfigureAwait(false);
                IsPaused = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resuming transcoding");
            }
        }
    }

    /// <summary>
    /// Stop throttler.
    /// </summary>
    /// <returns>A <see cref="Task"/>.</returns>
    public async Task Stop()
    {
        DisposeTimer();
        await UnpauseTranscoding().ConfigureAwait(false);
    }

    /// <summary>
    /// Dispose throttler.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose throttler.
    /// </summary>
    /// <param name="disposing">Disposing.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeTimer();
        }
    }

    private EncodingOptions GetOptions()
    {
        return _config.GetEncodingOptions();
    }

    private async void TimerCallback(object? state) => await EvaluateThrottleStateAsync().ConfigureAwait(false);

    /// <summary>
    /// Evaluates whether the transcoding process should currently be paused or running
    /// and sends the matching command. Invoked from the timer; internal for tests.
    /// </summary>
    /// <returns>A <see cref="Task"/>.</returns>
    internal async Task EvaluateThrottleStateAsync()
    {
        if (_job.HasExited)
        {
            DisposeTimer();
            return;
        }

        var options = GetOptions();

        if (options.EnableThrottling && IsThrottleAllowed(_job, Math.Max(options.ThrottleDelaySeconds, 60)))
        {
            await PauseTranscoding().ConfigureAwait(false);
        }
        else
        {
            await UnpauseTranscoding().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a control sequence to the transcoding process's standard input.
    /// Virtual so tests can observe commands without a real ffmpeg process.
    /// </summary>
    /// <param name="text">The control text to send.</param>
    /// <returns>A <see cref="Task"/>.</returns>
    protected virtual async Task SendProcessInputAsync(string text)
    {
        await _job.Process!.StandardInput.WriteAsync(text).ConfigureAwait(false);
    }

    private async Task PauseTranscoding()
    {
        if (!IsPaused)
        {
            var pauseKey = _mediaEncoder.IsPkeyPauseSupported ? "p" : "c";

            _logger.LogDebug("Sending pause command [{Key}] to ffmpeg", pauseKey);

            try
            {
                await SendProcessInputAsync(pauseKey).ConfigureAwait(false);
                IsPaused = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error pausing transcoding");
            }
        }
    }

    private bool IsThrottleAllowed(TranscodingJob job, int thresholdSeconds)
    {
        if (job.HasActiveSegmentWaiters)
        {
            // A segment request is blocked waiting for the transcoder to produce its
            // segment. Pausing now (or staying paused) would deadlock the stream: a
            // paused encoder never writes the segment, the response never completes,
            // and DownloadPositionTicks never advances to lift the throttle.
            _logger.LogDebug("Not throttling transcoder; a segment request is waiting on transcoder output");
            return false;
        }

        var bytesDownloaded = job.BytesDownloaded;
        var transcodingPositionTicks = job.TranscodingPositionTicks ?? 0;
        var downloadPositionTicks = job.DownloadPositionTicks ?? 0;

        var path = job.Path ?? throw new ArgumentException("Path can't be null.");

        var gapLengthInTicks = TimeSpan.FromSeconds(thresholdSeconds).Ticks;

        if (downloadPositionTicks > 0 && transcodingPositionTicks > 0)
        {
            // HLS - time-based consideration

            var targetGap = gapLengthInTicks;
            var gap = transcodingPositionTicks - downloadPositionTicks;

            if (gap < targetGap)
            {
                _logger.LogDebug("Not throttling transcoder gap {0} target gap {1}", gap, targetGap);
                return false;
            }

            _logger.LogDebug("Throttling transcoder gap {0} target gap {1}", gap, targetGap);
            return true;
        }

        if (bytesDownloaded > 0 && transcodingPositionTicks > 0)
        {
            // Progressive Streaming - byte-based consideration

            try
            {
                var bytesTranscoded = job.BytesTranscoded ?? _fileSystem.GetFileInfo(path).Length;

                // Estimate the bytes the transcoder should be ahead
                double gapFactor = gapLengthInTicks;
                gapFactor /= transcodingPositionTicks;
                var targetGap = bytesTranscoded * gapFactor;

                var gap = bytesTranscoded - bytesDownloaded;

                if (gap < targetGap)
                {
                    _logger.LogDebug("Not throttling transcoder gap {0} target gap {1} bytes downloaded {2}", gap, targetGap, bytesDownloaded);
                    return false;
                }

                _logger.LogDebug("Throttling transcoder gap {0} target gap {1} bytes downloaded {2}", gap, targetGap, bytesDownloaded);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting output size");
                return false;
            }
        }

        _logger.LogDebug("No throttle data for {Path}", path);
        return false;
    }

    private void DisposeTimer()
    {
        if (_timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }
}
