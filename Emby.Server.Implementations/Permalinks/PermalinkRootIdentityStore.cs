// SlopTank modification notice: added or changed by SlopTank on 2026-10-01.
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>Data access for registered content roots. No registration decisions live here.</summary>
internal sealed class PermalinkRootIdentityStore
{
    private readonly PermalinkAuthorityStore _authority;
    private readonly TimeProvider _timeProvider;

    public PermalinkRootIdentityStore(PermalinkAuthorityStore authority, TimeProvider timeProvider)
    {
        _authority = authority;
        _timeProvider = timeProvider;
    }

    /// <summary>Reads the identity of the authority that owns the database.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authority id.</returns>
    public async Task<Guid> ReadAuthorityIdAsync(CancellationToken cancellationToken)
    {
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        var path = Path.Combine(_authority.AuthorityRoot, "authority.json");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return CanonicalJson.Deserialize<PermalinkAuthorityDocument>(bytes, path).AuthorityId;
    }

    /// <summary>Finds a row by identity.</summary>
    /// <param name="rootIdentityId">The identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public Task<PermalinkRootIdentityRow?> FindByIdAsync(string rootIdentityId, CancellationToken cancellationToken)
        => FindAsync("root_identity_id = $key", rootIdentityId, cancellationToken);

    /// <summary>Finds a row by canonical root path.</summary>
    /// <param name="canonicalRoot">The canonical root path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public Task<PermalinkRootIdentityRow?> FindByRootAsync(string canonicalRoot, CancellationToken cancellationToken)
        => FindAsync("canonical_root = $key", canonicalRoot, cancellationToken);

    /// <summary>Inserts a new registration under an exact authorized write.</summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task InsertAsync(PermalinkRootIdentityRow row, CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            Guid.NewGuid(),
            "PermalinkRootIdentities",
            "INSERT",
            row.RootIdentityId,
            cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PermalinkRootIdentities
                (root_identity_id, generation, canonical_root, attestation, created_at)
            VALUES ($id, $generation, $root, $attestation, $created)
            """;
        command.Parameters.AddWithValue("$id", row.RootIdentityId);
        command.Parameters.AddWithValue("$generation", row.Generation);
        command.Parameters.AddWithValue("$root", row.CanonicalRoot);
        command.Parameters.AddWithValue("$attestation", row.Attestation);
        command.Parameters.AddWithValue(
            "$created",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PermalinkRootIdentityRow?> FindAsync(string where, string key, CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT root_identity_id, generation, canonical_root, attestation
              FROM PermalinkRootIdentities WHERE {where} ORDER BY generation DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PermalinkRootIdentityRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3));
    }
}
