using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// Serializes trickplay generation while allowing playback work to preempt background work.
/// </summary>
internal sealed class TrickplayGenerationQueue : IDisposable
{
    private readonly Lock _syncLock = new();
    private readonly SemaphoreSlim _resource = new(1, 1);
    private int _foregroundCount;
    private TaskCompletionSource _foregroundDrained = CreateCompletedSource();
    private CancellationTokenSource? _activeBackgroundCancellation;

    /// <summary>
    /// Acquires the trickplay generation resource.
    /// </summary>
    /// <param name="foreground">Whether the request is for an actively playing item.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>A lease for the generation resource.</returns>
    public async Task<Lease> AcquireAsync(bool foreground, CancellationToken cancellationToken)
    {
        try
        {
            if (foreground)
            {
                BeginForegroundRequest();
            }

            while (true)
            {
                if (!foreground)
                {
                    await WaitForForegroundRequestsAsync(cancellationToken).ConfigureAwait(false);
                }

                await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);

                CancellationTokenSource? backgroundCancellation = null;
                var retry = false;
                lock (_syncLock)
                {
                    if (!foreground && _foregroundCount > 0)
                    {
                        retry = true;
                    }
                    else if (!foreground)
                    {
                        backgroundCancellation = new CancellationTokenSource();
                        _activeBackgroundCancellation = backgroundCancellation;
                    }
                }

                if (retry)
                {
                    _resource.Release();
                    continue;
                }

                return new Lease(this, foreground, backgroundCancellation);
            }
        }
        catch
        {
            if (foreground)
            {
                EndForegroundRequest();
            }

            throw;
        }
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private void BeginForegroundRequest()
    {
        CancellationTokenSource? backgroundCancellation;
        lock (_syncLock)
        {
            if (_foregroundCount++ == 0)
            {
                _foregroundDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            backgroundCancellation = _activeBackgroundCancellation;
        }

        try
        {
            backgroundCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The background lease completed after it was observed above.
        }
        catch (AggregateException)
        {
            // Cancellation callbacks must not prevent foreground work from acquiring the resource.
        }
    }

    private void EndForegroundRequest()
    {
        TaskCompletionSource? foregroundDrained = null;
        lock (_syncLock)
        {
            if (--_foregroundCount == 0)
            {
                foregroundDrained = _foregroundDrained;
            }
        }

        foregroundDrained?.TrySetResult();
    }

    private async Task WaitForForegroundRequestsAsync(CancellationToken cancellationToken)
    {
        Task foregroundDrained;
        lock (_syncLock)
        {
            foregroundDrained = _foregroundDrained.Task;
        }

        await foregroundDrained.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Release(bool foreground, CancellationTokenSource? backgroundCancellation)
    {
        if (foreground)
        {
            EndForegroundRequest();
        }
        else
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_activeBackgroundCancellation, backgroundCancellation))
                {
                    _activeBackgroundCancellation = null;
                }
            }

            backgroundCancellation?.Dispose();
        }

        _resource.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_syncLock)
        {
            _activeBackgroundCancellation?.Dispose();
            _activeBackgroundCancellation = null;
        }

        _resource.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Represents exclusive access to the trickplay generation resource.
    /// </summary>
    internal sealed class Lease : IDisposable
    {
        private readonly TrickplayGenerationQueue _owner;
        private readonly bool _foreground;
        private readonly CancellationTokenSource? _backgroundCancellation;
        private int _disposed;

        public Lease(
            TrickplayGenerationQueue owner,
            bool foreground,
            CancellationTokenSource? backgroundCancellation)
        {
            _owner = owner;
            _foreground = foreground;
            _backgroundCancellation = backgroundCancellation;
        }

        /// <summary>
        /// Gets the token that is cancelled when foreground work preempts this lease.
        /// </summary>
        public CancellationToken PreemptionToken => _backgroundCancellation?.Token ?? CancellationToken.None;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_foreground, _backgroundCancellation);
            }
        }
    }
}
