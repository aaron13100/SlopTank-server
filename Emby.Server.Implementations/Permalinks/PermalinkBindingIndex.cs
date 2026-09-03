using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;

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
    /// <param name="authority">The permalink authority store.</param>
    /// <param name="timeProvider">The time provider.</param>
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
    /// <param name="permalinkId">The permalink id.</param>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="contentRoot">The verified content root.</param>
    /// <param name="capsuleId">The anchor capsule this binding currently resolves through.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task BindAsync(
        string permalinkId,
        Guid itemId,
        string contentRoot,
        Guid capsuleId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        // FirstAliasClaims is keyed by capsule, so more than one capsule can
        // claim the same alias -- which is exactly what happens when a file is
        // rewritten: the old capsule and the new one both claim it. The
        // resolver joins the alias to its claims with no tiebreak, so which
        // capsule a link resolves through becomes arbitrary, and picking the
        // superseded one means comparing the item's live anchor against an
        // anchor whose file no longer exists: 409 binding-replaced, forever.
        // Measured on production 2026-09-03 after converting an episode.
        //
        // The override names which capsule THIS binding resolves through, so
        // writing it whenever the alias has competing claims makes the choice
        // deterministic and current. It is what PromoteItemBindingsAsync
        // already writes for the promotion path, generalized to ordinary
        // re-binding. FirstAliasClaims itself is untouched: it stays the
        // immutable record of which capsule minted the alias.
        //
        // Scoped to the competing-claims case on purpose. An alias with a
        // single claim needs no tiebreak, and writing an override for it would
        // set IsCapsuleOverride on every binding in the system, which the
        // resolver reads as licence to skip its "is this alias still active for
        // this item" check.
        command.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        command.CommandText = """
            INSERT INTO PermalinkBindingCapsuleOverrides
                (PermalinkId, ItemId, capsule_id, created_at)
            SELECT $id, $item, $capsule, $created
             WHERE (SELECT COUNT(*) FROM FirstAliasClaims
                     WHERE permalink_id = $id) > 1
            ON CONFLICT(PermalinkId, ItemId) DO UPDATE SET
                capsule_id = excluded.capsule_id
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Deliberately no cleanup pass. An earlier version deleted the override
        // whenever the alias was down to a single claim, on the theory that the
        // tiebreak was no longer needed. But this is not the only writer:
        // PromoteItemBindingsAsync records an override to move an alias onto a
        // promoted item, and that alias can legitimately have one claim. The
        // cleanup deleted those on the next unrelated bind, which broke
        // resolving an sk- alias for an item with no provider id and failed the
        // permalink browser gate (13 tests to 12) on 2026-09-03; the promotion
        // rolled itself back. Writing an override we own is additive and cannot
        // remove behaviour that already worked; removing someone else's is not.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances only the derived current-path index after durable path adoption.
    /// </summary>
    /// <param name="anchorToken">The anchor token.</param>
    /// <param name="currentPath">The current path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
    /// <param name="permalinkId">The permalink id.</param>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<Guid> FindCapsuleIdAsync(
        string permalinkId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(
                       PermalinkBindingCapsuleOverrides.capsule_id,
                       FirstAliasClaims.capsule_id)
              FROM FirstAliasClaims
              JOIN PermalinkBindings
                ON PermalinkBindings.PermalinkId = FirstAliasClaims.permalink_id
              LEFT JOIN PermalinkBindingCapsuleOverrides
                ON PermalinkBindingCapsuleOverrides.PermalinkId = PermalinkBindings.PermalinkId
               AND PermalinkBindingCapsuleOverrides.ItemId = PermalinkBindings.ItemId
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

    /// <summary>Returns whether an item already has durable permalink identity.</summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<bool> HasItemBindingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!_authority.IsConfigured)
        {
            return false;
        }

        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
              FROM PermalinkBindings
             WHERE ItemId = $item
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Returns whether an alias was durably issued even when no live binding remains.</summary>
    /// <param name="permalinkId">The permalink identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the authority retains an immutable first-alias claim.</returns>
    public async Task<bool> IsKnownAliasAsync(
        string permalinkId,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
              FROM FirstAliasClaims
             WHERE permalink_id = $id
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", permalinkId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Moves every derived alias binding to a promoted item and its verified capsule.
    /// </summary>
    /// <param name="oldItemId">The old item id.</param>
    /// <param name="promotedItemId">The promoted item id.</param>
    /// <param name="promotedPath">The promoted path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task PromoteItemBindingsAsync(
        Guid oldItemId,
        Guid promotedItemId,
        string promotedPath,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.capsule_id
              FROM FirstAliasClaims f
              JOIN PermalinkBindings b ON b.PermalinkId = f.permalink_id
              JOIN AnchorTokenCapsules a ON a.capsule_id = f.capsule_id
             WHERE b.ItemId = $promoted
               AND a.current_path = $path
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$promoted", promotedItemId.ToString("D"));
        command.Parameters.AddWithValue("$path", promotedPath);
        var promotedCapsule = (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "promotion-target-unprotected",
                $"Promoted item '{promotedItemId}' has no verified permalink capsule.");

        command.Parameters.AddWithValue("$old", oldItemId.ToString("D"));
        command.CommandText = """
            INSERT INTO PermalinkBindings
                (PermalinkId, ItemId, ContentRoot, VerifiedToken, VerifiedAt, CreatedAt)
            SELECT PermalinkId, $promoted, ContentRoot, VerifiedToken, VerifiedAt, CreatedAt
              FROM PermalinkBindings
             WHERE ItemId = $old
            ON CONFLICT(PermalinkId, ItemId) DO UPDATE SET
                ContentRoot = excluded.ContentRoot,
                VerifiedToken = excluded.VerifiedToken,
                VerifiedAt = excluded.VerifiedAt
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.AddWithValue("$capsule", promotedCapsule);
        command.Parameters.AddWithValue(
            "$created",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        command.CommandText = """
            INSERT INTO PermalinkBindingCapsuleOverrides
                (PermalinkId, ItemId, capsule_id, created_at)
            SELECT PermalinkId, $promoted, $capsule, $created
              FROM PermalinkBindings
             WHERE ItemId = $old
            ON CONFLICT(PermalinkId, ItemId) DO UPDATE SET
                capsule_id = excluded.capsule_id
            """;
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "DELETE FROM PermalinkBindings WHERE ItemId = $old";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "DELETE FROM PermalinkBindingCapsuleOverrides WHERE ItemId = $old";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes a deleted item's rebuildable live bindings while retaining capsule history.</summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RemoveItemBindingsAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM PermalinkBindingCapsuleOverrides WHERE ItemId = $item;
            DELETE FROM PermalinkBindings WHERE ItemId = $item;
            """;
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Advances all derived aliases for one item after a controlled content commit.
    /// </summary>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="contentRoot">The verified content root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
    /// <param name="permalinkId">The permalink id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
                   a.anchor_token, a.binding_id, a.current_path,
                   o.capsule_id IS NOT NULL
              FROM PermalinkBindings b
              JOIN FirstAliasClaims f ON f.permalink_id = b.PermalinkId
              LEFT JOIN PermalinkBindingCapsuleOverrides o
                ON o.PermalinkId = b.PermalinkId
               AND o.ItemId = b.ItemId
              JOIN AnchorTokenCapsules a
                ON a.capsule_id = COALESCE(o.capsule_id, f.capsule_id)
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
                Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.GetBoolean(7)));
        }

        return result;
    }

    /// <summary>
    /// Returns every binding held for one item, without consulting its content.
    /// </summary>
    /// <remarks>
    /// The usual route to a binding is to compute the item's identity from its
    /// bytes and look the resulting alias up. That is impossible for an item
    /// whose file has already gone, which is exactly the state of an item the
    /// library is deleting because its file disappeared. This lookup answers
    /// from what the authority already recorded, so a deletion never has to
    /// hash content that no longer exists.
    /// </remarks>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item's bindings, ordered by alias; empty when unbound.</returns>
    public async Task<IReadOnlyList<PermalinkResolutionBinding>> FindItemBindingsAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.PermalinkId, b.ItemId, a.capsule_id, b.ContentRoot,
                   a.anchor_token, a.binding_id, a.current_path,
                   o.capsule_id IS NOT NULL
              FROM PermalinkBindings b
              JOIN FirstAliasClaims f ON f.permalink_id = b.PermalinkId
              LEFT JOIN PermalinkBindingCapsuleOverrides o
                ON o.PermalinkId = b.PermalinkId
               AND o.ItemId = b.ItemId
              JOIN AnchorTokenCapsules a
                ON a.capsule_id = COALESCE(o.capsule_id, f.capsule_id)
             WHERE b.ItemId = $item
             ORDER BY b.PermalinkId
            """;
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
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
                Guid.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.GetBoolean(7)));
        }

        return result;
    }

    /// <summary>Returns one exact derived alias/item binding.</summary>
    /// <param name="permalinkId">The permalink id.</param>
    /// <param name="itemId">The Jellyfin item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkResolutionBinding> FindResolutionBindingAsync(
        string permalinkId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var matches = await FindResolutionBindingsAsync(permalinkId, cancellationToken)
            .ConfigureAwait(false);
        return matches.SingleOrDefault(value => value.ItemId.Equals(itemId))
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
