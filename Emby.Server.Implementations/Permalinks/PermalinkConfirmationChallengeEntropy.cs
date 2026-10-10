// SlopTank modification notice: added or changed by SlopTank on 2026-10-10.
using System;
using System.Security.Cryptography;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Supplies cryptographic identifiers and secret bytes for confirmation challenges.</summary>
internal interface IPermalinkConfirmationChallengeEntropy
{
    /// <summary>Creates an unpredictable challenge identifier.</summary>
    /// <returns>The identifier.</returns>
    Guid NewId();

    /// <summary>Creates the requested number of cryptographically strong secret bytes.</summary>
    /// <param name="count">The number of bytes.</param>
    /// <returns>The secret bytes.</returns>
    byte[] GetBytes(int count);
}

/// <summary>Implements confirmation-challenge entropy with the operating system cryptographic generator.</summary>
internal sealed class CryptographicPermalinkConfirmationChallengeEntropy : IPermalinkConfirmationChallengeEntropy
{
    /// <inheritdoc />
    public Guid NewId() => new(RandomNumberGenerator.GetBytes(16));

    /// <inheritdoc />
    public byte[] GetBytes(int count) => RandomNumberGenerator.GetBytes(count);
}
