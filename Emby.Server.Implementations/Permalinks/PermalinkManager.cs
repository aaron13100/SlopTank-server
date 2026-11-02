// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-08-03, 2026-09-08, 2026-09-09, 2026-10-01, 2026-10-14, 2026-10-31, 2026-11-02.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Creates and resolves logical content links using existing metadata and the
/// application database. Media bytes and filesystem identities are not involved.
/// </summary>
public sealed class PermalinkManager : IPermalinkManager
{
    private readonly MediaPermalinkRepository _repository;
    private readonly MediaContentIdentifier _identifiers;
    private readonly LegacyMediaPermalinkRepository _legacy;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly TimeProvider _clock;

    /// <summary>Initializes the portable metadata-to-file association service.</summary>
    public PermalinkManager(MediaPermalinkRepository repository, MediaContentIdentifier identifiers, LegacyMediaPermalinkRepository legacy,
        ILibraryManager library, IUserManager users, TimeProvider clock)
    {
        _repository = repository;
        _identifiers = identifiers;
        _legacy = legacy;
        _library = library;
        _users = users;
        _clock = clock;
    }

    /// <summary>Gets the identifier for this movie, series, season or specific episode.</summary>
    /// <param name="videoFile">The server's existing library metadata record.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The same public response and URL namespaces used by existing clients.</returns>
    public async Task<PermalinkResponse> EnsurePermalinkIdsAsync(BaseItem videoFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoFile);
        var binding = await BindMetadataAsync(videoFile, cancellationToken).ConfigureAwait(false);
        return new PermalinkResponse(1, binding.Ids, binding.Ids[0]);
    }

    /// <summary>Looks up current library files for an identifier, applying normal user visibility.</summary>
    /// <param name="identifier">A public logical content identifier.</param>
    /// <param name="userId">The authenticated user requesting the library item.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>Visible logical candidates, each retaining its ordinary library versions.</returns>
    public async Task<IReadOnlyList<Candidate>> GetVideoFileAsync(string identifier, Guid userId, CancellationToken cancellationToken)
    {
        if (!MediaContentIdentifier.IsPublicAlias(identifier))
        {
            return Array.Empty<Candidate>();
        }

        var user = _users.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return Array.Empty<Candidate>();
        }

        await AssociateMetadataMatchesAsync(identifier, userId, cancellationToken).ConfigureAwait(false);
        var mappings = await _repository.ReadByAliasesAsync([identifier], cancellationToken).ConfigureAwait(false);
        if (mappings.Count == 0)
        {
            await ImportLegacyAliasAsync(identifier, userId, cancellationToken).ConfigureAwait(false);
            mappings = await _repository.ReadByAliasesAsync([identifier], cancellationToken).ConfigureAwait(false);
        }
        var candidates = new List<Candidate>(mappings.Count);
        foreach (var mapping in mappings)
        {
            if (!mapping.Ids.Contains(identifier, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var providerAlias in mapping.Ids.Where(alias => MediaContentIdentifier.ParseProviderAlias(alias) is not null))
            {
                await AssociateMetadataMatchesAsync(providerAlias, userId, cancellationToken).ConfigureAwait(false);
            }

            var visible = new List<BaseItem>();
            foreach (var itemId in await _repository.ReadItemIdsAsync(mapping.ContentId, cancellationToken).ConfigureAwait(false))
            {
                var item = _library.GetItemById<BaseItem>(itemId, user);
                if (item is null)
                {
                    continue;
                }

                var current = await _repository.ReadForItemAsync(itemId, cancellationToken).ConfigureAwait(false);
                var metadata = await ReadMetadataAsync(item, createSeriesIdentity: false, cancellationToken).ConfigureAwait(false);
                if (current.ContentId != mapping.ContentId || metadata.Kind != mapping.Kind || IdentityChanged(current, metadata))
                {
                    continue;
                }

                if (_library.GetItemById<BaseItem>(itemId, user) is not null)
                {
                    item.PermalinkContentId = mapping.ContentId;
                    visible.Add(item);
                }
            }

            if (visible.Count > 0)
            {
                candidates.Add(new Candidate(mapping.ContentId, mapping.Kind, visible.AsReadOnly()));
            }
        }

        return candidates.AsReadOnly();
    }

    private async Task AssociateMetadataMatchesAsync(string alias, Guid userId, CancellationToken token)
    {
        var provider = MediaContentIdentifier.ParseProviderAlias(alias);
        var user = _users.GetUserById(userId);
        if (provider is null || user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return;
        }

        if ((await _repository.ReadByAliasesAsync([alias], token).ConfigureAwait(false)).Count > 1)
        {
            // Preserve an imported ambiguous alias for the existing chooser.
            // Looking it up must not silently consolidate distinct titles.
            return;
        }

        var items = _library.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            HasAnyProviderIds = new Dictionary<string, string[]> { [provider.Provider] = [provider.Value] },
            IncludeItemTypes = provider.Kind.HasValue ? [provider.Kind.Value]
                : [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.BoxSet, BaseItemKind.Video, BaseItemKind.MusicVideo]
        });
        foreach (var item in items.Where(value => !value.ExtraType.HasValue))
        {
            token.ThrowIfCancellationRequested();
            await BindMetadataAsync(item, token).ConfigureAwait(false);
        }
    }

    private async Task ImportLegacyAliasAsync(string alias, Guid userId, CancellationToken token)
    {
        var legacy = await _legacy.ReadAsync(alias, token).ConfigureAwait(false);
        var user = _users.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return;
        }

        foreach (var association in legacy.GroupBy(value => value.ItemId))
        {
            var providerAliases = association.Select(value => value.Alias)
                .Where(value => MediaContentIdentifier.ParseProviderAlias(value) is not null).Distinct(StringComparer.Ordinal).ToArray();
            var items = new Dictionary<Guid, BaseItem>();
            var original = _library.GetItemById<BaseItem>(association.Key, user);
            if (original is not null && !original.ExtraType.HasValue)
            {
                var metadata = await ReadMetadataAsync(original, createSeriesIdentity: true, token).ConfigureAwait(false);
                if (providerAliases.Length == 0 || metadata.PublicAliases.Intersect(providerAliases, StringComparer.Ordinal).Any())
                {
                    items[original.Id] = original;
                }
            }

            // A conversion can have left the old path-derived library ID behind.
            // Recover through recorded provider metadata, never by old file bytes.
            foreach (var providerAlias in providerAliases)
            {
                await AssociateMetadataMatchesAsync(providerAlias, userId, token).ConfigureAwait(false);
                foreach (var binding in await _repository.ReadByAliasesAsync([providerAlias], token).ConfigureAwait(false))
                {
                    foreach (var itemId in await _repository.ReadItemIdsAsync(binding.ContentId, token).ConfigureAwait(false))
                    {
                        var item = _library.GetItemById<BaseItem>(itemId, user);
                        if (item is not null)
                        {
                            items[item.Id] = item;
                        }
                    }
                }
            }

            foreach (var item in items.Values)
            {
                var binding = await BindMetadataAsync(item, token).ConfigureAwait(false);
                await _repository.ImportAliasAsync(new PermalinkBindingRequest(item.Id, binding.ContentId,
                    binding.ContentId, binding.Kind, [alias], null, _clock.GetUtcNow().UtcDateTime), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<MediaPermalinkBinding> BindMetadataAsync(BaseItem item, CancellationToken token)
    {
        if (item.Id == Guid.Empty || item.ExtraType.HasValue)
        {
            throw new PermalinkException(PermalinkErrorKind.Ineligible, "item-ineligible", "A persisted library title is required; extras and trailers do not have title permalinks.");
        }

        var metadata = await ReadMetadataAsync(item, createSeriesIdentity: true, token).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await _repository.ReadForItemAsync(item.Id, token).ConfigureAwait(false);
            var keys = metadata.PublicAliases.Concat(metadata.CoordinateKey is null ? Array.Empty<string>() : [metadata.CoordinateKey]).ToArray();
            var matches = await _repository.ReadByAliasesAsync(keys, token).ConfigureAwait(false);
            if (matches.Any(value => value.Kind != metadata.Kind) || matches.Count > 1)
            {
                throw new PermalinkException(PermalinkErrorKind.Conflict, "metadata-identity-conflict", "The library metadata identifies more than one title. Correct its provider or episode metadata before associating this file.", itemId: item.Id);
            }

            var contentId = matches.Count == 1 ? matches[0].ContentId
                : current.ContentId.HasValue && !IdentityChanged(current, metadata) ? current.ContentId.Value
                : _identifiers.CreateContentId();
            var inheritedContentId = current.ContentId.HasValue && current.ContentId != contentId
                && current.Kind == metadata.Kind && current.Keys.All(key => key.StartsWith("sk-", StringComparison.Ordinal))
                ? current.ContentId : null;
            var aliases = metadata.PublicAliases.Append(MediaContentIdentifier.OpaqueAlias(contentId))
                .Concat(inheritedContentId.HasValue ? current.PublicAliases : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (current.ContentId == contentId
                && aliases.All(alias => current.PublicAliases.Contains(alias, StringComparer.Ordinal))
                && (metadata.CoordinateKey is null || current.Keys.Contains(metadata.CoordinateKey, StringComparer.Ordinal)))
            {
                item.PermalinkContentId = contentId;
                return new MediaPermalinkBinding(contentId, metadata.Kind, current.PublicAliases, current.Keys);
            }

            try
            {
                await _repository.BindAsync(new PermalinkBindingRequest(item.Id, current.ContentId, contentId,
                    metadata.Kind, aliases, metadata.CoordinateKey, _clock.GetUtcNow().UtcDateTime, inheritedContentId), token).ConfigureAwait(false);
                var saved = await _repository.ReadForItemAsync(item.Id, token).ConfigureAwait(false);
                if (saved.ContentId != contentId || saved.PublicAliases.Count == 0)
                {
                    throw MappingChanged();
                }

                item.PermalinkContentId = contentId;
                return new MediaPermalinkBinding(contentId, metadata.Kind, saved.PublicAliases, saved.Keys);
            }
            catch (PermalinkException exception) when (exception.Code == "mapping-changed" && attempt < 2)
            {
                // Re-read the database winner; a retry must not overwrite its association.
                continue;
            }
        }

        throw MappingChanged();
    }

    private async Task<MediaContentIdentity> ReadMetadataAsync(BaseItem item, bool createSeriesIdentity, CancellationToken token)
    {
        var series = item switch
        {
            Episode episode when episode.SeriesId != Guid.Empty => _library.GetItemById<Series>(episode.SeriesId),
            Season season when season.SeriesId != Guid.Empty => _library.GetItemById<Series>(season.SeriesId),
            _ => null
        };
        Guid? seriesContentId = null;
        if (series is not null)
        {
            seriesContentId = createSeriesIdentity
                ? (await BindMetadataAsync(series, token).ConfigureAwait(false)).ContentId
                : (await _repository.ReadForItemAsync(series.Id, token).ConfigureAwait(false)).ContentId;
        }

        return _identifiers.Identify(item, seriesContentId, series?.DisplayOrder);
    }

    private static bool IdentityChanged(PermalinkItemBinding previous, MediaContentIdentity current)
    {
        if (previous.Kind is not null && previous.Kind != current.Kind)
        {
            return true;
        }

        var oldProviderIds = previous.PublicAliases.Where(value => !value.StartsWith("sk-", StringComparison.Ordinal)).ToArray();
        if (oldProviderIds.Length > 0 && current.PublicAliases.Count > 0)
        {
            return !oldProviderIds.Intersect(current.PublicAliases, StringComparer.Ordinal).Any();
        }

        var oldCoordinates = previous.Keys.Where(value => value.StartsWith("metadata:v1:", StringComparison.Ordinal)).ToArray();
        return current.CoordinateKey is not null && oldCoordinates.Length > 0
            && !oldCoordinates.Contains(current.CoordinateKey, StringComparer.Ordinal);
    }

    private static PermalinkException MappingChanged() => new(PermalinkErrorKind.Conflict,
        "mapping-changed", "The media metadata changed while its permalink association was being saved.");

    /// <summary>A logical title and its current visible library versions.</summary>
    public sealed record Candidate(Guid ContentId, string Kind, IReadOnlyList<BaseItem> Items);
}
