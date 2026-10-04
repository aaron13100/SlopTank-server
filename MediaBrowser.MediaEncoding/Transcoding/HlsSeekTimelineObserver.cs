// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Transcoding;

internal static class HlsSeekTimelineObserver
{
    private static readonly TimeSpan _observationTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan _probeCleanupTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(100);

    public static bool ShouldObserve(
        TranscodingJobType jobType,
        bool isVideoRequest,
        string? outputVideoCodec,
        long? requestedStartTimeTicks)
    {
        return jobType == TranscodingJobType.Hls
            && isVideoRequest
            && EncodingHelper.IsCopyCodec(outputVideoCodec)
            && requestedStartTimeTicks > 0;
    }

    public static async Task ObserveAsync(
        Context context,
        string firstSegmentPath,
        string probePath,
        TranscodingJob transcodingJob,
        ILogger logger,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? _observationTimeout);

        Process? probe = null;
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var probeStarted = false;
        int? probePid = null;
        try
        {
            if (!TryGetNextTsSegmentPath(firstSegmentPath, transcodingJob.Path, out var nextSegmentPath, out var layoutFailure))
            {
                logger.LogWarning(
                    "HLS seek timeline observation unavailable: {Reason}. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid}",
                    layoutFailure,
                    context.MediaSourceId,
                    context.RequestId,
                    context.JobId,
                    context.FfmpegPid);
                return;
            }

            while (!File.Exists(firstSegmentPath)
                || (!File.Exists(nextSegmentPath) && !transcodingJob.HasExited))
            {
                await Task.Delay(_pollInterval, deadline.Token).ConfigureAwait(false);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = probePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-read_intervals");
            startInfo.ArgumentList.Add("%+#64");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=codec_type,start_time");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(firstSegmentPath);

            probe = new Process { StartInfo = startInfo };
            if (!probe.Start())
            {
                logger.LogWarning(
                    "HLS seek timeline observation failed: ffprobe did not start. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePath={ProbePath}",
                    context.MediaSourceId,
                    context.RequestId,
                    context.JobId,
                    context.FfmpegPid,
                    probePath);
                return;
            }

            probeStarted = true;
            probePid = probe.Id;
            standardOutput = probe.StandardOutput.ReadToEndAsync();
            standardError = probe.StandardError.ReadToEndAsync();
            await probe.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).WaitAsync(deadline.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);

            if (probe.ExitCode != 0)
            {
                logger.LogWarning(
                    "HLS seek timeline observation failed: ffprobe exited with code {ExitCode}: {Error}. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid} Segment={SegmentPath}",
                    probe.ExitCode,
                    error.Trim(),
                    context.MediaSourceId,
                    context.RequestId,
                    context.JobId,
                    context.FfmpegPid,
                    probePid,
                    firstSegmentPath);
                return;
            }

            if (!TryReadStreamStartTimes(output, out var videoPts, out var audioPts, out var parseFailure))
            {
                logger.LogWarning(
                    "HLS seek timeline observation failed: {Reason}. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid} Segment={SegmentPath} ProbeError={ProbeError}",
                    parseFailure,
                    context.MediaSourceId,
                    context.RequestId,
                    context.JobId,
                    context.FfmpegPid,
                    probePid,
                    firstSegmentPath,
                    error.Trim());
                return;
            }

            var requestedStartSeconds = TimeSpan.FromTicks(context.RequestedStartTimeTicks).TotalSeconds;
            logger.LogInformation(
                "HLS seek timeline output timestamp deltas: MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} RequestedStartTimeTicks={RequestedStartTimeTicks} InputModifier={InputModifier} SubtitleDeliveryMethod={SubtitleDeliveryMethod} CopyTimestamps={CopyTimestamps} OutputVideoPts={OutputVideoPts} OutputAudioPts={OutputAudioPts} RequestedMinusOutputVideoPts={RequestedMinusOutputVideoPts} OutputAudioMinusVideoPts={OutputAudioMinusVideoPts}",
                context.MediaSourceId,
                context.RequestId,
                context.JobId,
                context.FfmpegPid,
                context.RequestedStartTimeTicks,
                context.InputModifier,
                context.SubtitleDeliveryMethod,
                context.CopyTimestamps,
                videoPts,
                audioPts,
                requestedStartSeconds - videoPts,
                audioPts - videoPts);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (probe is not null && probeStarted)
            {
                await KillAndReapProbeAsync(probe, standardOutput, standardError, context, probePid, logger).ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "HLS seek timeline observation timed out after {TimeoutSeconds} seconds. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid} Segment={SegmentPath}",
                    (timeout ?? _observationTimeout).TotalSeconds,
                    context.MediaSourceId,
                    context.RequestId,
                    context.JobId,
                    context.FfmpegPid,
                    probePid,
                    firstSegmentPath);
            }
        }
        catch (Exception exception)
        {
            if (probe is not null && probeStarted)
            {
                await KillAndReapProbeAsync(probe, standardOutput, standardError, context, probePid, logger).ConfigureAwait(false);
            }

            logger.LogWarning(
                exception,
                "HLS seek timeline observation failed: {Reason}. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid} Segment={SegmentPath}",
                exception.Message,
                context.MediaSourceId,
                context.RequestId,
                context.JobId,
                context.FfmpegPid,
                probePid,
                firstSegmentPath);
        }
        finally
        {
            probe?.Dispose();
        }
    }

    private static async Task KillAndReapProbeAsync(
        Process probe,
        Task<string>? standardOutput,
        Task<string>? standardError,
        Context context,
        int? probePid,
        ILogger logger)
    {
        try
        {
            probe.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to terminate ffprobe after observer cancellation. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid}",
                context.MediaSourceId,
                context.RequestId,
                context.JobId,
                context.FfmpegPid,
                probePid);
        }

        using var cleanupDeadline = new CancellationTokenSource(_probeCleanupTimeout);
        try
        {
            await probe.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
            var drainTasks = Task.WhenAll(
                standardOutput ?? Task.FromResult(string.Empty),
                standardError ?? Task.FromResult(string.Empty));
            await drainTasks.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cleanupDeadline.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "Timed out reaping ffprobe after observer cancellation. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FFmpegPid} ProbePid={ProbePid} CleanupTimeoutSeconds={CleanupTimeoutSeconds}",
                context.MediaSourceId,
                context.RequestId,
                context.JobId,
                context.FfmpegPid,
                probePid,
                _probeCleanupTimeout.TotalSeconds);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to reap ffprobe after observer cancellation. MediaSourceId={MediaSourceId} RequestId={RequestId} JobId={JobId} FFmpegPid={FfmpegPid} ProbePid={ProbePid}",
                context.MediaSourceId,
                context.RequestId,
                context.JobId,
                context.FfmpegPid,
                probePid);
        }
    }

    private static bool TryGetNextTsSegmentPath(string firstSegmentPath, string? playlistPath, out string nextSegmentPath, out string failure)
    {
        nextSegmentPath = string.Empty;
        failure = string.Empty;

        if (!Path.GetExtension(firstSegmentPath).Equals(".ts", StringComparison.OrdinalIgnoreCase))
        {
            failure = $"Unsupported HLS segment layout; expected a numbered .ts segment, got '{Path.GetFileName(firstSegmentPath)}'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(playlistPath))
        {
            failure = "Unsupported HLS segment layout; transcoding job has no playlist path to identify its segment prefix";
            return false;
        }

        var prefix = Path.GetFileNameWithoutExtension(playlistPath);
        var fileName = Path.GetFileNameWithoutExtension(firstSegmentPath);
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(fileName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index == int.MaxValue)
        {
            failure = $"Unsupported HLS segment layout; segment '{Path.GetFileName(firstSegmentPath)}' does not match the numbered prefix '{prefix}'";
            return false;
        }

        var nextFileName = string.Concat(prefix, (index + 1).ToString(CultureInfo.InvariantCulture), ".ts");
        nextSegmentPath = Path.Combine(Path.GetDirectoryName(firstSegmentPath) ?? string.Empty, nextFileName);
        return true;
    }

    private static bool TryReadStreamStartTimes(string output, out double videoPts, out double audioPts, out string failure)
    {
        videoPts = 0;
        audioPts = 0;
        failure = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(output);
            if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            {
                failure = "ffprobe JSON has no streams array";
                return false;
            }

            var hasVideo = false;
            var hasAudio = false;
            var invalidTimestamp = string.Empty;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecTypeElement)
                    || codecTypeElement.ValueKind != JsonValueKind.String
                    || !stream.TryGetProperty("start_time", out var startTimeElement)
                    || startTimeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var codecType = codecTypeElement.GetString();
                if (codecType is not ("video" or "audio"))
                {
                    continue;
                }

                var startTime = startTimeElement.GetString();
                if (startTime is null
                    || !double.TryParse(startTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)
                    || !double.IsFinite(pts))
                {
                    invalidTimestamp = $"ffprobe returned an invalid {codecType} start_time '{startTime}'";
                    break;
                }

                if (codecType == "video" && !hasVideo)
                {
                    videoPts = pts;
                    hasVideo = true;
                }
                else if (codecType == "audio" && !hasAudio)
                {
                    audioPts = pts;
                    hasAudio = true;
                }
            }

            if (invalidTimestamp.Length > 0)
            {
                failure = invalidTimestamp;
                return false;
            }

            if (!hasVideo || !hasAudio)
            {
                failure = !hasVideo ? "ffprobe returned no video stream start_time" : "ffprobe returned no audio stream start_time";
                return false;
            }

            return true;
        }
        catch (JsonException exception)
        {
            failure = $"ffprobe returned invalid JSON: {exception.Message}";
            return false;
        }
    }

    internal sealed record Context(
        string MediaSourceId,
        Guid RequestId,
        string JobId,
        int FfmpegPid,
        long RequestedStartTimeTicks,
        string InputModifier,
        SubtitleDeliveryMethod SubtitleDeliveryMethod,
        bool CopyTimestamps);
}
