// SlopTank modification notice: added or changed by SlopTank on 2026-09-19.
using System;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Owns one connection-local write capability.</summary>
internal sealed class PermalinkAuthorityWriteScope : IDisposable
{
    private Action _release;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="PermalinkAuthorityWriteScope"/> class.</summary>
    /// <param name="mac">The bound capability MAC.</param>
    /// <param name="nonce">The authorization nonce.</param>
    /// <param name="release">The connection-local cleanup.</param>
    public PermalinkAuthorityWriteScope(string mac, string nonce, Action release)
    {
        Mac = mac;
        Nonce = nonce;
        _release = release;
    }

    /// <summary>Gets the MAC stored in the narrow authorization row.</summary>
    public string Mac { get; }

    /// <summary>Gets the nonce identifying the matching authorization row.</summary>
    public string Nonce { get; }

    /// <summary>Adds cleanup that runs before the connection-local scope is released.</summary>
    /// <param name="release">The additional cleanup.</param>
    public void AddRelease(Action release) => _release = release + _release;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release();
    }
}
