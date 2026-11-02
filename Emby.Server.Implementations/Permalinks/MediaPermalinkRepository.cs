// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Emby.Server.Implementations.Permalinks;

internal sealed record PermalinkItemBinding(Guid? ContentId, string? Kind, IReadOnlyList<string> PublicAliases, IReadOnlyList<string> Keys);

internal sealed record MediaPermalinkBinding(Guid ContentId, string Kind, IReadOnlyList<string> Ids, IReadOnlyList<string> Keys);

internal sealed record PermalinkBindingRequest(
    Guid ItemId,
    Guid? ExpectedContentId,
    Guid ContentId,
    string Kind,
    IReadOnlyList<string> PublicAliases,
    string? CoordinateKey,
    DateTime NowUtc,
    Guid? InheritedContentId = null,
    IReadOnlyList<string>? LegacyAliases = null,
    IReadOnlyList<string>? RetiredAliases = null);

/// <summary>
/// Persists permalink identity mappings and immutable aliases in the application database.
/// </summary>
public sealed class MediaPermalinkRepository
{
    private static readonly HashSet<string> _allowedKinds = new(StringComparer.Ordinal)
    {
        "movie", "series", "season", "episode", "boxset", "video", "musicvideo"
    };

    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>Initializes storage in the existing application database.</summary>
    /// <param name="dbContextFactory">The application's database context factory.</param>
    public MediaPermalinkRepository(IDbContextFactory<JellyfinDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    internal async Task<PermalinkItemBinding> ReadForItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        // Aliases are append-only. Reading the item association first identifies
        // a complete immutable alias set without taking a SQLite write lock.
        var contentId = await context.BaseItems
            .Where(item => item.Id == itemId)
            .Select(item => item.PermalinkContentId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!contentId.HasValue)
        {
            return new PermalinkItemBinding(null, null, Array.Empty<string>(), Array.Empty<string>());
        }

        var rows = await context.MediaPermalinkAliases
            .Where(alias => alias.ContentId == contentId.Value)
            .OrderBy(alias => alias.Ordinal)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (rows.Length == 0)
        {
            throw new PermalinkException(PermalinkErrorKind.Corrupt, "stored-alias-missing", $"Logical content '{contentId}' has no stored permalink aliases.", itemId: itemId);
        }

        ValidateStoredRows(contentId.Value, rows);
        var kind = rows.Select(row => row.Kind).Distinct(StringComparer.Ordinal).SingleOrDefault();
        return new PermalinkItemBinding(
            contentId,
            kind,
            rows.Where(row => row.IsPublic && !row.DeletedAtUtc.HasValue).OrderBy(row => row.Ordinal).Select(row => row.Alias).ToArray(),
            rows.Where(row => !row.DeletedAtUtc.HasValue).OrderBy(row => row.IsPublic ? row.Ordinal : int.MaxValue).Select(row => row.Alias).ToArray());
    }

    internal async Task<IReadOnlyList<MediaPermalinkBinding>> ReadByAliasesAsync(
        IReadOnlyCollection<string> aliases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(aliases);
        if (aliases.Count == 0)
        {
            return Array.Empty<MediaPermalinkBinding>();
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var aliasKeys = aliases.Distinct(StringComparer.Ordinal).ToArray();
        var matchedContentIds = await context.MediaPermalinkAliases
            .Where(alias => !alias.DeletedAtUtc.HasValue && aliasKeys.Contains(alias.Alias))
            .Select(alias => alias.ContentId)
            .Distinct()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (matchedContentIds.Length == 0)
        {
            return Array.Empty<MediaPermalinkBinding>();
        }

        var rows = await context.MediaPermalinkAliases
            .Where(alias => matchedContentIds.Contains(alias.ContentId))
            .OrderBy(alias => alias.ContentId)
            .ThenBy(alias => alias.Ordinal)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        var bindings = rows.GroupBy(row => row.ContentId)
            .Select(group =>
            {
                var contentRows = group.ToArray();
                ValidateStoredRows(group.Key, contentRows);
                var kind = contentRows.Select(row => row.Kind).Distinct(StringComparer.Ordinal).Single();
                return new MediaPermalinkBinding(
                    group.Key,
                    kind,
                    contentRows.Where(row => row.IsPublic && !row.DeletedAtUtc.HasValue).OrderBy(row => row.Ordinal).Select(row => row.Alias).ToArray(),
                    contentRows.Where(row => !row.DeletedAtUtc.HasValue).OrderBy(row => row.IsPublic ? row.Ordinal : int.MaxValue).Select(row => row.Alias).ToArray());
            })
            .ToArray();
        return bindings;
    }

    internal async Task<IReadOnlyList<Guid>> ReadItemIdsAsync(Guid contentId, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.BaseItems
            .Where(item => item.PermalinkContentId == contentId)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Preserves an already-issued legacy alias without merging distinct titles.</summary>
    internal async Task ImportAliasAsync(PermalinkBindingRequest request, CancellationToken token)
    {
        ValidateRequest(request);
        await using var context = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(token).ConfigureAwait(false);
        if (!await context.BaseItems.AnyAsync(item => item.Id == request.ItemId && item.PermalinkContentId == request.ContentId, token).ConfigureAwait(false))
        {
            throw MappingChanged();
        }

        var rows = await context.MediaPermalinkAliases.Where(alias => alias.ContentId == request.ContentId).ToArrayAsync(token).ConfigureAwait(false);
        ValidateStoredRows(request.ContentId, rows);
        if (rows.Length == 0 || rows.Any(row => row.Kind != request.Kind))
        {
            throw MappingChanged();
        }

        var ordinal = rows.Count(row => row.IsPublic);
        foreach (var alias in request.PublicAliases.Where(alias => !rows.Any(row => row.Alias == alias)))
        {
            context.MediaPermalinkAliases.Add(new MediaPermalinkAlias
            {
                Alias = alias,
                ContentId = request.ContentId,
                Kind = request.Kind,
                IsPublic = true,
                Ordinal = ordinal++,
                CreatedAtUtc = request.NowUtc,
                UpdatedAtUtc = request.NowUtc
            });
        }

        await context.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    internal async Task BindAsync(PermalinkBindingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var item = await context.BaseItems.SingleOrDefaultAsync(row => row.Id == request.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null || item.PermalinkContentId != request.ExpectedContentId)
        {
            throw MappingChanged();
        }

        var itemType = BaseItemMapper.GetType(item.Type);
        if (!MatchesKind(itemType, request.Kind))
        {
            throw new PermalinkException(PermalinkErrorKind.Ineligible, "item-kind-mismatch", $"Library item type '{item.Type}' does not match permalink kind '{request.Kind}'.", itemId: request.ItemId);
        }

        var contentRows = await context.MediaPermalinkAliases
            .Where(alias => alias.ContentId == request.ContentId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateStoredRows(request.ContentId, contentRows);
        if (request.InheritedContentId.HasValue)
        {
            var inherited = await context.MediaPermalinkAliases.Where(alias => alias.ContentId == request.InheritedContentId.Value)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            ValidateStoredRows(request.InheritedContentId.Value, inherited);
            if (inherited.Length == 0 || inherited.Any(alias => alias.Kind != request.Kind || !alias.IsPublic
                || !alias.Alias.StartsWith("sk-", StringComparison.Ordinal) || !request.PublicAliases.Contains(alias.Alias, StringComparer.Ordinal)))
            {
                throw new PermalinkException(PermalinkErrorKind.Conflict, "metadata-merge-conflict",
                    "Only an unidentified title's existing opaque aliases may follow its newly identified metadata.", itemId: request.ItemId);
            }
        }

        var requestedAliases = request.PublicAliases.ToArray();
        if (await context.MediaPermalinkAliases.AnyAsync(
                alias => !alias.DeletedAtUtc.HasValue && requestedAliases.Contains(alias.Alias) && alias.ContentId != request.ContentId
                    && alias.ContentId != request.InheritedContentId,
                cancellationToken).ConfigureAwait(false))
        {
            // The composite alias key preserves imported legacy ambiguities,
            // but ordinary creation must join the existing logical title.
            // Re-read the winning association instead of creating a duplicate.
            throw MappingChanged();
        }

        if (contentRows.Any(row => !string.Equals(row.Kind, request.Kind, StringComparison.Ordinal)))
        {
            throw new PermalinkException(PermalinkErrorKind.Conflict, "content-kind-mismatch", "A logical content identity cannot contain aliases of different kinds.", itemId: request.ItemId);
        }

        if (request.CoordinateKey is not null)
        {
            var privateKeyRows = await context.MediaPermalinkAliases
                .Where(alias => !alias.DeletedAtUtc.HasValue && alias.Alias == request.CoordinateKey)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (privateKeyRows.Any(row => row.ContentId != request.ContentId || row.IsPublic))
            {
                throw new PermalinkException(PermalinkErrorKind.Conflict, "coordinate-key-conflict", "The private coordinate key is already assigned to another alias binding.", itemId: request.ItemId);
            }
        }

        var existingPublicAliases = contentRows.Where(row => row.IsPublic).ToDictionary(row => row.Alias, StringComparer.Ordinal);
        if (contentRows.Any(row => request.PublicAliases.Contains(row.Alias, StringComparer.Ordinal) && !row.IsPublic))
        {
            throw new PermalinkException(PermalinkErrorKind.Conflict, "alias-kind-conflict", "A private coordinate key cannot be changed into a public alias.", itemId: request.ItemId);
        }

        var orderedAliases = (request.LegacyAliases ?? Array.Empty<string>()).Concat(request.PublicAliases).Distinct(StringComparer.Ordinal).ToArray();
        var desiredKeys = orderedAliases.Concat(request.CoordinateKey is null ? Array.Empty<string>() : [request.CoordinateKey]).ToHashSet(StringComparer.Ordinal);
        var aliasStateChanged = false;
        foreach (var row in contentRows)
        {
            if (desiredKeys.Contains(row.Alias) && row.DeletedAtUtc.HasValue)
            {
                row.DeletedAtUtc = null;
                row.UpdatedAtUtc = request.NowUtc;
                aliasStateChanged = true;
            }
            else if (!row.DeletedAtUtc.HasValue && request.RetiredAliases?.Contains(row.Alias, StringComparer.Ordinal) == true)
            {
                row.DeletedAtUtc = request.NowUtc;
                row.UpdatedAtUtc = request.NowUtc;
                aliasStateChanged = true;
            }
        }

        var newRows = new List<MediaPermalinkAlias>();
        var nextOrdinal = existingPublicAliases.Count;
        foreach (var alias in orderedAliases)
        {
            if (existingPublicAliases.ContainsKey(alias))
            {
                continue;
            }

            newRows.Add(new MediaPermalinkAlias
            {
                Alias = alias,
                ContentId = request.ContentId,
                Kind = request.Kind,
                IsPublic = true,
                Ordinal = nextOrdinal++,
                CreatedAtUtc = request.NowUtc,
                UpdatedAtUtc = request.NowUtc
            });
        }

        if (request.CoordinateKey is not null && !contentRows.Any(row => row.Alias == request.CoordinateKey))
        {
            newRows.Add(new MediaPermalinkAlias
            {
                Alias = request.CoordinateKey,
                ContentId = request.ContentId,
                Kind = request.Kind,
                IsPublic = false,
                Ordinal = null,
                CreatedAtUtc = request.NowUtc,
                UpdatedAtUtc = request.NowUtc
            });
        }

        if (item.PermalinkContentId == request.ContentId && newRows.Count == 0 && !aliasStateChanged)
        {
            // Metadata lookup is common during playback and must not rewrite
            // an unchanged row or force a WAL flush for each resolution.
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var changedRows = await context.BaseItems
            .Where(row => row.Id == request.ItemId && row.PermalinkContentId == request.ExpectedContentId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.PermalinkContentId, request.ContentId), cancellationToken)
            .ConfigureAwait(false);
        if (changedRows != 1)
        {
            throw MappingChanged();
        }

        if (request.InheritedContentId.HasValue)
        {
            // Identification joins an initially unknown logical record to an
            // already-known title. Preserve its issued URLs and versions in
            // the same transaction, so a crash cannot strand the old link.
            await context.BaseItems.Where(row => row.PermalinkContentId == request.InheritedContentId.Value)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.PermalinkContentId, request.ContentId), cancellationToken).ConfigureAwait(false);
        }

        context.MediaPermalinkAliases.AddRange(newRows);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new PermalinkException(PermalinkErrorKind.Conflict, "mapping-changed", "Another permalink binding changed while this mapping was being saved.", exception, itemId: request.ItemId);
        }
    }

    private static void ValidateRequest(PermalinkBindingRequest request)
    {
        if (request.ItemId == Guid.Empty || request.ContentId == Guid.Empty ||
            !_allowedKinds.Contains(request.Kind) || request.NowUtc.Kind != DateTimeKind.Utc ||
            request.PublicAliases is null || request.PublicAliases.Count == 0 ||
            request.PublicAliases.Any(alias => !MediaContentIdentifier.IsPublicAlias(alias)) ||
            request.PublicAliases.Distinct(StringComparer.Ordinal).Count() != request.PublicAliases.Count ||
            request.PublicAliases.Any(alias => alias.Length > 512) ||
            request.CoordinateKey is { Length: 0 or > 512 } ||
            request.LegacyAliases?.Any(alias => !MediaContentIdentifier.IsPublicAlias(alias)) == true ||
            request.RetiredAliases?.Any(alias => string.IsNullOrEmpty(alias) || alias.StartsWith("sk-", StringComparison.Ordinal)
                || request.PublicAliases.Contains(alias, StringComparer.Ordinal) || request.CoordinateKey == alias) == true ||
            (request.InheritedContentId.HasValue && (request.InheritedContentId != request.ExpectedContentId || request.InheritedContentId == request.ContentId)) ||
            (request.CoordinateKey is not null && request.PublicAliases.Contains(request.CoordinateKey, StringComparer.Ordinal)))
        {
            throw new PermalinkException(PermalinkErrorKind.Ineligible, "invalid-binding", "The permalink binding request is invalid.");
        }
    }

    private static void ValidateStoredRows(Guid contentId, IReadOnlyCollection<MediaPermalinkAlias> rows)
    {
        if (contentId == Guid.Empty ||
            (rows.Count > 0 && !rows.Any(row => row.IsPublic)) ||
            rows.Any(row => row.ContentId != contentId || row.RecordVersion != 1 || !_allowedKinds.Contains(row.Kind) ||
                string.IsNullOrWhiteSpace(row.Alias) || row.Alias.Length > 512 ||
                (row.IsPublic ? row.Ordinal is null or < 0 || !MediaContentIdentifier.IsPublicAlias(row.Alias) : row.Ordinal is not null)) ||
            rows.Select(row => row.Kind).Distinct(StringComparer.Ordinal).Count() > 1 ||
            rows.Where(row => row.IsPublic).GroupBy(row => row.Ordinal).Any(group => group.Count() != 1) ||
            rows.Where(row => row.IsPublic).Select(row => row.Ordinal!.Value).Order().Where((ordinal, index) => ordinal != index).Any())
        {
            throw new PermalinkException(PermalinkErrorKind.Corrupt, "stored-alias-invalid", $"Stored permalink aliases for content '{contentId}' have unsupported or inconsistent metadata.");
        }
    }

    private static bool MatchesKind(Type? itemType, string kind)
    {
        if (itemType is null)
        {
            return false;
        }

        return kind switch
        {
            "movie" => typeof(Movie).IsAssignableFrom(itemType),
            "series" => typeof(Series).IsAssignableFrom(itemType),
            "season" => typeof(Season).IsAssignableFrom(itemType),
            "episode" => typeof(Episode).IsAssignableFrom(itemType),
            "boxset" => typeof(BoxSet).IsAssignableFrom(itemType),
            "musicvideo" => typeof(MusicVideo).IsAssignableFrom(itemType),
            "video" => typeof(Video).IsAssignableFrom(itemType)
                && !typeof(Movie).IsAssignableFrom(itemType)
                && !typeof(Episode).IsAssignableFrom(itemType)
                && !typeof(MusicVideo).IsAssignableFrom(itemType),
            _ => false
        };
    }

    private static PermalinkException MappingChanged() =>
        new(PermalinkErrorKind.Conflict, "mapping-changed", "The library item's permalink mapping changed before the update could be saved.");
}
