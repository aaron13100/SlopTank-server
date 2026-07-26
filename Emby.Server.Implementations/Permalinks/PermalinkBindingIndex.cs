using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>
    /// Finds the capsule owning one verified derived alias/item binding.
    /// </summary>
    public async Task<Guid> FindCapsuleIdAsync(
        string permalinkId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FirstAliasClaims.capsule_id
              FROM FirstAliasClaims
              JOIN PermalinkBindings
                ON PermalinkBindings.PermalinkId = FirstAliasClaims.permalink_id
             WHERE PermalinkBindings.PermalinkId = $id
               AND PermalinkBindings.ItemId = $item
            """;
        command.Parameters.AddWithValue("$id", permalinkId);
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        var value = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null
            ? throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "binding-missing",
                $"Verified binding '{permalinkId}' for item '{itemId}' is missing.")
            : Guid.Parse(value);
    }

    /// <summary>
    /// Advances all derived aliases for one item after a controlled content commit.
    /// </summary>
    public async Task UpdateContentRootAsync(
        Guid itemId,
        string contentRoot,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PermalinkBindings SET ContentRoot = $root
             WHERE ItemId = $item
            """;
        command.Parameters.AddWithValue("$root", contentRoot);
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns all derived bindings for one exact alias.</summary>
    public async Task<IReadOnlyList<PermalinkResolutionBinding>> FindResolutionBindingsAsync(
        string permalinkId,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.PermalinkId, b.ItemId, a.capsule_id, b.ContentRoot,
                   a.anchor_token, a.current_path
              FROM PermalinkBindings b
              JOIN FirstAliasClaims f ON f.permalink_id = b.PermalinkId
              JOIN AnchorTokenCapsules a ON a.capsule_id = f.capsule_id
             WHERE b.PermalinkId = $id
             ORDER BY b.ItemId
            """;
        command.Parameters.AddWithValue("$id", permalinkId);
        var result = new List<PermalinkResolutionBinding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PermalinkResolutionBinding(
                reader.GetString(0),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return result;
    }

    /// <summary>Returns one exact derived alias/item binding.</summary>
    public async Task<PermalinkResolutionBinding> FindResolutionBindingAsync(
        string permalinkId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var matches = await FindResolutionBindingsAsync(permalinkId, cancellationToken)
            .ConfigureAwait(false);
        return matches.SingleOrDefault(value => value.ItemId == itemId)
            ?? throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "binding-missing",
                $"Binding '{permalinkId}' for item '{itemId}' is missing.");
    }

    private static Guid GenerateGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}

internal sealed record PermalinkResolutionBinding(
    string PermalinkId,
    Guid ItemId,
    Guid CapsuleId,
    string ContentRoot,
    string AnchorToken,
    string CurrentPath);
