using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns the rebuildable SQLite alias/item and current-path indexes.
/// </summary>
internal sealed class PermalinkBindingIndex
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkBindingIndex"/> class.
    /// </summary>
    public PermalinkBindingIndex(
        PermalinkAuthorityStore authority,
        TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Replaces one derived alias/item binding after durable verification.
    /// </summary>
    public async Task BindAsync(
        string permalinkId,
        Guid itemId,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PermalinkBindings
                (PermalinkId, ItemId, ContentRoot, VerifiedToken, VerifiedAt, CreatedAt)
            VALUES ($id, $item, $root, $token, $verified, $created)
            ON CONFLICT(PermalinkId, ItemId) DO UPDATE SET
                ContentRoot = excluded.ContentRoot,
                VerifiedToken = excluded.VerifiedToken,
                VerifiedAt = excluded.VerifiedAt
            """;
        var now = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$id", permalinkId);
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        command.Parameters.AddWithValue("$root", contentRoot);
        command.Parameters.AddWithValue("$token", GenerateGuid().ToString("D"));
        command.Parameters.AddWithValue("$verified", now);
        command.Parameters.AddWithValue("$created", now);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances only the derived current-path index after durable path adoption.
    /// </summary>
    public async Task UpdateCurrentPathAsync(
        string anchorToken,
        string currentPath,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AnchorTokenCapsules
               SET current_path = $path
             WHERE anchor_token = $token
            """;
        command.Parameters.AddWithValue("$path", currentPath);
        command.Parameters.AddWithValue("$token", anchorToken);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "anchor-path-missing",
                $"Cannot advance current path for unknown anchor '{anchorToken}'.");
        }
    }

    private static Guid GenerateGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}
