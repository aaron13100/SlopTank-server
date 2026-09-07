using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaBrowser.Controller.Session;

/// <summary>
/// Maintains an O(1) health read over session-manager transcode observations.
/// </summary>
public sealed class TranscodeProgressHealthTracker
{
    private readonly Lock _sync = new();
    private readonly Dictionary<string, DateTime> _zeroProgressSince = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _earliestZeroProgress;

    /// <summary>
    /// Records the newest tracked state for a device transcode.
    /// </summary>
    /// <param name="deviceId">Device whose transcode state changed.</param>
    /// <param name="hasProgress">Whether any output progress has been observed.</param>
    /// <param name="observedUtc">Observation time.</param>
    public void Report(string deviceId, bool hasProgress, DateTime observedUtc)
    {
        // Existing session cleanup can carry an empty legacy device id. Health
        // tracking must never make those established paths throw.
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        lock (_sync)
        {
            if (hasProgress)
            {
                Remove(deviceId);
                return;
            }

            if (_zeroProgressSince.TryAdd(deviceId, observedUtc)
                && (!_earliestZeroProgress.HasValue || observedUtc < _earliestZeroProgress.Value))
            {
                _earliestZeroProgress = observedUtc;
            }
        }
    }

    /// <summary>
    /// Stops tracking a completed or abandoned transcode.
    /// </summary>
    /// <param name="deviceId">Device whose transcode ended.</param>
    public void Clear(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        lock (_sync)
        {
            Remove(deviceId);
        }
    }

    /// <summary>
    /// Gets whether any transcode has remained at zero progress since the threshold.
    /// </summary>
    /// <param name="thresholdUtc">Latest acceptable first-zero observation.</param>
    /// <returns><see langword="true"/> if the earliest zero observation is old enough.</returns>
    public bool HasZeroProgressSince(DateTime thresholdUtc)
    {
        lock (_sync)
        {
            return _earliestZeroProgress.HasValue
                   && _earliestZeroProgress.Value <= thresholdUtc;
        }
    }

    private void Remove(string deviceId)
    {
        if (!_zeroProgressSince.Remove(deviceId, out var removed))
        {
            return;
        }

        if (_earliestZeroProgress != removed)
        {
            return;
        }

        _earliestZeroProgress = null;
        foreach (var observed in _zeroProgressSince.Values)
        {
            if (!_earliestZeroProgress.HasValue || observed < _earliestZeroProgress.Value)
            {
                _earliestZeroProgress = observed;
            }
        }
    }
}
