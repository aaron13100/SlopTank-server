// SlopTank modification notice: added or changed by SlopTank on 2026-10-01, 2026-10-03, 2026-10-07.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

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

    /// <summary>Gets the opaque cache namespace of the currently selected authority.</summary>
    public string AuthorityNamespace => _authority.CacheNamespace;

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

    /// <summary>Reads the authority id only while the expected cache namespace remains selected.</summary>
    /// <param name="authorityNamespace">The opaque authority namespace captured by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The authority id.</returns>
    public async Task<Guid> ReadAuthorityIdAsync(string authorityNamespace, CancellationToken cancellationToken)
    {
        RequireNamespace(authorityNamespace);
        await _authority.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        RequireNamespace(authorityNamespace);
        var path = Path.Combine(_authority.AuthorityRoot, "authority.json");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        RequireNamespace(authorityNamespace);
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

    /// <summary>Finds a row by root only while the expected authority remains selected.</summary>
    /// <param name="canonicalRoot">The canonical root path.</param>
    /// <param name="authorityNamespace">The opaque authority namespace captured by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public Task<PermalinkRootIdentityRow?> FindByRootAsync(
        string canonicalRoot,
        string authorityNamespace,
        CancellationToken cancellationToken)
        => FindAsync("canonical_root = $key", canonicalRoot, authorityNamespace, cancellationToken);

    /// <summary>Finds a row by id only while the expected authority remains selected.</summary>
    /// <param name="rootIdentityId">The identity.</param>
    /// <param name="authorityNamespace">The opaque authority namespace captured by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row, or null.</returns>
    public Task<PermalinkRootIdentityRow?> FindByIdAsync(
        string rootIdentityId,
        string authorityNamespace,
        CancellationToken cancellationToken)
        => FindAsync("root_identity_id = $key", rootIdentityId, authorityNamespace, cancellationToken);

    /// <summary>Lists every canonical root ever registered, including roots no longer configured.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The distinct canonical roots in ordinal order.</returns>
    public async Task<IReadOnlyList<string>> ListCanonicalRootsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT canonical_root FROM PermalinkRootIdentities ORDER BY canonical_root COLLATE BINARY";
        var roots = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            roots.Add(reader.GetString(0));
        }

        return roots;
    }

    /// <summary>Inserts a new registration under an exact authorized write.</summary>
    /// <param name="row">The row.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task InsertAsync(PermalinkRootIdentityRow row, CancellationToken cancellationToken)
        => await InsertCoreAsync(row, authorityNamespace: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Inserts a registration only into the expected authority namespace.</summary>
    /// <param name="row">The row.</param>
    /// <param name="authorityNamespace">The opaque authority namespace captured by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public Task InsertAsync(
        PermalinkRootIdentityRow row,
        string authorityNamespace,
        CancellationToken cancellationToken)
        => InsertCoreAsync(row, authorityNamespace, cancellationToken);

    private async Task InsertCoreAsync(
        PermalinkRootIdentityRow row,
        string? authorityNamespace,
        CancellationToken cancellationToken)
    {
        if (authorityNamespace is not null)
        {
            RequireNamespace(authorityNamespace);
        }

        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (authorityNamespace is not null)
        {
            // From this point the connection is pinned to the expected database. A later ResetTo
            // can change the selected namespace, but it cannot redirect this insert into it.
            RequireNamespace(authorityNamespace);
        }

        using var scope = await _authority.BeginAuthorizedWriteAsync(
            connection,
            Guid.NewGuid(),
            "PermalinkRootIdentities",
            "INSERT",
            row.RootIdentityId,
            cancellationToken).ConfigureAwait(false);
        if (authorityNamespace is not null)
        {
            // Do not begin a root write after the caller's authority selection has gone stale.
            // Once execution starts below, the open connection keeps the write on the old database.
            RequireNamespace(authorityNamespace);
        }

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
        => await FindAsync(where, key, authorityNamespace: null, cancellationToken).ConfigureAwait(false);

    private async Task<PermalinkRootIdentityRow?> FindAsync(
        string where,
        string key,
        string? authorityNamespace,
        CancellationToken cancellationToken)
    {
        if (authorityNamespace is not null)
        {
            RequireNamespace(authorityNamespace);
        }

        await using var connection = await _authority.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (authorityNamespace is not null)
        {
            RequireNamespace(authorityNamespace);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT root_identity_id, generation, canonical_root, attestation
              FROM PermalinkRootIdentities WHERE {where} ORDER BY generation DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (authorityNamespace is not null)
            {
                RequireNamespace(authorityNamespace);
            }

            return null;
        }

        var row = new PermalinkRootIdentityRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3));
        if (authorityNamespace is not null)
        {
            RequireNamespace(authorityNamespace);
        }

        return row;
    }

    private void RequireNamespace(string expected)
    {
        if (!string.Equals(expected, AuthorityNamespace, StringComparison.Ordinal))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                PermalinkRootIdentityRegistry.UnknownCode,
                "The permalink authority changed while a root identity was being checked.");
        }
    }
}
