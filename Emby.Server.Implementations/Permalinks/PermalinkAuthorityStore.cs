// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-07-30, 2026-08-29, 2026-09-03, 2026-09-09.
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns the single authority allocation domain and its transactional elections.
/// </summary>
internal sealed class PermalinkAuthorityStore : IDisposable
{
    /// <summary>How long a successful writability proof is trusted for.</summary>
    private static readonly long ValidationIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly IPermalinkAtomicFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _provisionLock = new(1, 1);
    private readonly string? _configuredRoot;
    private bool _provisioned;

    /// <summary>
    /// When the authority volume was last proven writable, as UTC ticks.
    ///
    /// The liveness probe below is real I/O on the media volume, and it used to
    /// run on EVERY call. Measured in-process on 2026-09-03, that step cost
    /// 111-919 ms of a 525-1704 ms ensure, and a single watch link performs
    /// three ensures: six write probes and three directory sweeps to open one
    /// video. Re-proving the same volume writable several times a second buys
    /// nothing, because any operation that actually needs the volume fails on
    /// its own I/O anyway.
    /// </summary>
    private long _lastValidatedTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkAuthorityStore"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="fileSystem">The durable permalink filesystem.</param>
    /// <param name="timeProvider">The time provider.</param>
    public PermalinkAuthorityStore(
        IConfiguration configuration,
        IPermalinkAtomicFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        _configuredRoot = configuration["Permalinks:AuthorityRoot"];
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the configured authority root.
    /// </summary>
    public string Root
    {
        get
        {
            if (!IsConfigured)
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Unavailable,
                    "authority-not-configured",
                    "Permalinks:AuthorityRoot is not configured.");
            }

            return _configuredRoot!;
        }
    }

    /// <summary>
    /// Gets a value indicating whether permalink authority is configured.
    /// </summary>
    internal bool IsConfigured => !string.IsNullOrWhiteSpace(_configuredRoot);

    private string AuthorityRoot => Path.Combine(Root, ".sloptank", "permalinks");

    private string DatabasePath => Path.Combine(AuthorityRoot, "authority.db");

    internal string IssuedRoot => Path.Combine(AuthorityRoot, "issued");

    /// <inheritdoc />
    public void Dispose()
    {
        _provisionLock.Dispose();
    }

    /// <summary>
    /// Provisions and validates the authority before any content-root mutation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        return EnsureProvisionedAsync(cancellationToken);
    }

    /// <summary>
    /// Reads an existing global anchor election.
    /// </summary>
    /// <param name="anchorToken">The anchor token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkGenesisReservation?> FindByAnchorAsync(
        string anchorToken,
        CancellationToken cancellationToken)
    {
        await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_token, capsule_id, binding_id, root_path, capsule_path,
                   item_kind, content_root, current_path, header_json, event_json,
                   anchor_json, issued_id, issuance_nonce, created_at
              FROM AnchorTokenCapsules
             WHERE anchor_token = $anchor_token
            """;
        command.Parameters.AddWithValue("$anchor_token", anchorToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader, isWinner: false)
            : null;
    }

    /// <summary>
    /// Reads an existing global capsule election independently of the live filesystem anchor.
    /// </summary>
    /// <param name="capsuleId">The durable capsule identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching reservation, or <see langword="null"/> when it is unknown.</returns>
    public async Task<PermalinkGenesisReservation?> FindByCapsuleAsync(
        Guid capsuleId,
        CancellationToken cancellationToken)
    {
        await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_token, capsule_id, binding_id, root_path, capsule_path,
                   item_kind, content_root, current_path, header_json, event_json,
                   anchor_json, issued_id, issuance_nonce, created_at
              FROM AnchorTokenCapsules
             WHERE capsule_id = $capsule_id
            """;
        command.Parameters.AddWithValue("$capsule_id", capsuleId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader, isWinner: false)
            : null;
    }

    /// <summary>
    /// Finds the genesis occupying an exact current path, regardless of anchor token.
    /// </summary>
    /// <param name="path">The filesystem path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkGenesisReservation?> FindByCurrentPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_token, capsule_id, binding_id, root_path, capsule_path,
                   item_kind, content_root, current_path, header_json, event_json,
                   anchor_json, issued_id, issuance_nonce, created_at
              FROM AnchorTokenCapsules
             WHERE current_path = $path
            """;
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadReservation(reader, isWinner: false)
            : null;
    }

    /// <summary>
    /// Atomically elects one genesis for a stable anchor.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task<PermalinkGenesisReservation> ReserveGenesisAsync(
        PermalinkGenesisCandidate candidate,
        CancellationToken cancellationToken)
    {
        await EnsureProvisionedAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO AnchorTokenCapsules
                    (anchor_token, capsule_id, binding_id, root_path, capsule_path,
                     item_kind, content_root, current_path, header_json, event_json,
                     anchor_json, issued_id, issuance_nonce, created_at)
                VALUES
                    ($anchor_token, $capsule_id, $binding_id, $root_path, $capsule_path,
                     $item_kind, $content_root, $current_path, $header_json, $event_json,
                     $anchor_json, $issued_id, $issuance_nonce, $created_at)
                """;
            AddCandidateParameters(insert, candidate);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (inserted == 1)
            {
                await PermalinkAuthoritySchema.InsertGenesisClaimsAsync(
                    connection,
                    transaction,
                    candidate,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT anchor_token, capsule_id, binding_id, root_path, capsule_path,
                   item_kind, content_root, current_path, header_json, event_json,
                   anchor_json, issued_id, issuance_nonce, created_at
              FROM AnchorTokenCapsules
             WHERE anchor_token = $anchor_token
            """;
        select.Parameters.AddWithValue("$anchor_token", candidate.AnchorToken);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Unavailable,
                "anchor-election-missing",
                $"Anchor election disappeared for '{candidate.AnchorToken}'.");
        }

        var reservation = ReadReservation(
            reader,
            string.Equals(
                reader.GetString(1),
                candidate.CapsuleId.ToString("D"),
                StringComparison.Ordinal));
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reservation;
    }

    private async Task EnsureProvisionedAsync(CancellationToken cancellationToken)
    {
        if (_provisioned)
        {
            ValidateProvisionedAuthorityIfDue();
            return;
        }

        await _provisionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_provisioned)
            {
                ValidateProvisionedAuthorityIfDue();
                return;
            }

            _fileSystem.CreateDirectoryDurable(IssuedRoot);
            _fileSystem.CleanupAbandonedPublications(IssuedRoot);
            var authorityPath = Path.Combine(AuthorityRoot, "authority.json");
            if (!File.Exists(authorityPath))
            {
                var authority = new PermalinkAuthorityDocument(
                    GenerateGuid(),
                    UtcNow());
                try
                {
                    await _fileSystem.PublishImmutableAsync(
                        authorityPath,
                        CanonicalJson.Serialize(authority),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (PermalinkException exception)
                    when (exception.Code == "publish-exclusive")
                {
                    // Another authority-owner process published the same election target.
                }
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await PermalinkAuthoritySchema.ApplyAsync(connection, cancellationToken).ConfigureAwait(false);
            _provisioned = true;
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SqliteException)
        {
            throw AuthorityUnavailable(exception);
        }
        finally
        {
            _provisionLock.Release();
        }
    }

    /// <summary>
    /// Proves the authority volume still writable, at most once per interval.
    ///
    /// The check exists to fail fast and clearly when the media volume goes
    /// away, rather than surfacing as a confusing error deeper in a mutation.
    /// An interval preserves that: a detached volume is still detected within
    /// a second, and every operation that touches it continues to fail on its
    /// own I/O in the meantime. What it stops is charging a user's click for a
    /// liveness probe that a previous click already paid for.
    ///
    /// A failed probe does NOT refresh the stamp, so once the volume is
    /// unhealthy every subsequent call re-probes and keeps throwing.
    /// </summary>
    private void ValidateProvisionedAuthorityIfDue()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastValidatedTicks);
        if (last != 0 && now - last < ValidationIntervalTicks)
        {
            return;
        }

        ValidateProvisionedAuthority();
        Interlocked.Exchange(ref _lastValidatedTicks, now);
    }

    private void ValidateProvisionedAuthority()
    {
        try
        {
            _fileSystem.ProbeDirectoryWriteAccess(Root);
            _fileSystem.CleanupAbandonedPublications(IssuedRoot);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            throw AuthorityUnavailable(exception);
        }
    }

    /// <summary>
    /// Publishes one immutable record into the authority's own issuance storage.
    ///
    /// Callers must route writes below <see cref="IssuedRoot"/> through here rather than
    /// composing the path and calling the file system directly: the liveness probe in
    /// <see cref="ValidateProvisionedAuthorityIfDue"/> is interval-limited, so between probes
    /// an unwritable authority is only observable as the raw filesystem failure this method
    /// translates. Without it that failure reaches the client as an opaque 500 rather than
    /// the documented 503.
    /// </summary>
    /// <param name="fileName">The file name to publish below the issuance root.</param>
    /// <param name="contents">The canonical bytes to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task PublishIssuanceAsync(
        string fileName,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        try
        {
            await _fileSystem.PublishImmutableAsync(
                Path.Combine(IssuedRoot, fileName),
                contents,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PermalinkException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException)
        {
            throw AuthorityUnavailable(exception);
        }
    }

    private PermalinkException AuthorityUnavailable(Exception exception)
    {
        return new PermalinkException(
            PermalinkErrorKind.Unavailable,
            "authority-unavailable",
            $"Permalink issuance authority '{Root}' is unavailable ({exception.Message}).",
            exception);
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=True");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = OperatingSystem.IsMacOS()
            ? "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA fullfsync=ON; PRAGMA checkpoint_fullfsync=ON;"
            : "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Permanently elects one mutation for an exact verified predecessor.
    /// </summary>
    /// <param name="capsuleId">The logical capsule identifier.</param>
    /// <param name="predecessorRoot">The predecessor root.</param>
    /// <param name="operationId">The durable operation identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ClaimMutationAsync(
        Guid capsuleId,
        string predecessorRoot,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MutationClaims
                (capsule_id, predecessor_root, operation_id, created_at)
            VALUES ($capsule, $root, $operation, $created)
            ON CONFLICT(capsule_id, predecessor_root) DO NOTHING
            """;
        command.Parameters.AddWithValue("$capsule", capsuleId.ToString("D"));
        command.Parameters.AddWithValue("$root", predecessorRoot);
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        command.Parameters.AddWithValue("$created", UtcNow());
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = """
            SELECT operation_id FROM MutationClaims
             WHERE capsule_id = $capsule AND predecessor_root = $root
            """;
        var winner = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(winner, operationId.ToString("D"), StringComparison.Ordinal))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "mutation-conflict",
                $"Content predecessor '{predecessorRoot}' is already claimed by '{winner}'.");
        }
    }

    private static void AddCandidateParameters(
        SqliteCommand command,
        PermalinkGenesisCandidate candidate)
    {
        command.Parameters.AddWithValue("$anchor_token", candidate.AnchorToken);
        command.Parameters.AddWithValue("$capsule_id", candidate.CapsuleId.ToString("D"));
        command.Parameters.AddWithValue("$binding_id", candidate.BindingId.ToString("D"));
        command.Parameters.AddWithValue("$root_path", candidate.RootPath);
        command.Parameters.AddWithValue("$capsule_path", candidate.CapsulePath);
        command.Parameters.AddWithValue("$item_kind", candidate.ItemKind);
        command.Parameters.AddWithValue("$content_root", candidate.ContentRoot);
        command.Parameters.AddWithValue("$current_path", candidate.CurrentPath);
        command.Parameters.AddWithValue("$header_json", Encoding.UTF8.GetString(candidate.HeaderJson.Span));
        command.Parameters.AddWithValue("$event_json", Encoding.UTF8.GetString(candidate.EventJson.Span));
        command.Parameters.AddWithValue("$anchor_json", Encoding.UTF8.GetString(candidate.AnchorJson.Span));
        command.Parameters.AddWithValue("$issued_id", candidate.Issuance?.Id ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$issuance_nonce",
            candidate.Issuance?.IssuanceNonce.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$created_at", candidate.CreatedAt);
    }

    private static PermalinkGenesisReservation ReadReservation(
        SqliteDataReader reader,
        bool isWinner)
    {
        return new PermalinkGenesisReservation(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            Encoding.UTF8.GetBytes(reader.GetString(8)),
            Encoding.UTF8.GetBytes(reader.GetString(9)),
            Encoding.UTF8.GetBytes(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
            reader.GetString(13),
            isWinner);
    }

    private string UtcNow()
    {
        return _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
    }

    private static Guid GenerateGuid()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}
