// SlopTank modification notice: added or changed by SlopTank on 2026-07-26, 2026-07-29, 2026-08-03, 2026-09-08, 2026-09-09, 2026-10-01, 2026-10-14, 2026-10-31, 2026-11-02, 2026-11-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Permalinks;
using MediaPermalinkBinding = Emby.Server.Implementations.Permalinks.MediaPermalinkRepository.MediaPermalinkBinding;
using PermalinkBindingRequest = Emby.Server.Implementations.Permalinks.MediaPermalinkRepository.PermalinkBindingRequest;
using PermalinkItemBinding = Emby.Server.Implementations.Permalinks.MediaPermalinkRepository.PermalinkItemBinding;

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

    /// <summary>Initializes a new instance of the <see cref="PermalinkManager"/> class.</summary>
    /// <param name="repository">The application database repository for logical title mappings.</param>
    /// <param name="identifiers">The metadata identity adapter.</param>
    /// <param name="legacy">The reader for existing legacy permalink aliases.</param>
    /// <param name="library">The existing library metadata and visibility service.</param>
    /// <param name="users">The existing user lookup service.</param>
    /// <param name="clock">The clock used for persisted timestamps.</param>
    public PermalinkManager(
        MediaPermalinkRepository repository,
        MediaContentIdentifier identifiers,
        LegacyMediaPermalinkRepository legacy,
        ILibraryManager library,
        IUserManager users,
        TimeProvider clock)
    {
        _repository = repository;
        _identifiers = identifiers;
        _legacy = legacy;
        _library = library;
        _users = users;
        _clock = clock;
    }

    /// <summary>Gets the identifier for this movie, series, season or specific episode.</summary>
    /// <param name="item">The server's existing library metadata record.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The same public response and URL namespaces used by existing clients.</returns>
    public async Task<PermalinkResponse> EnsurePermalinkIdsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var binding = await BindMetadataAsync(item, cancellationToken).ConfigureAwait(false);
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
        var candidates = await ReadMappedCandidatesAsync(identifier, userId, cancellationToken).ConfigureAwait(false);
        if (candidates.Count > 0)
        {
            return candidates;
        }

        // A prior mapping can outlive its last library record. Give the saved
        // legacy metadata one recovery attempt even when that dormant mapping
        // still exists; its presence alone does not mean a file was resolved.
        await ImportLegacyAliasAsync(identifier, userId, cancellationToken).ConfigureAwait(false);
        return await ReadMappedCandidatesAsync(identifier, userId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Candidate>> ReadMappedCandidatesAsync(string identifier, Guid userId, CancellationToken cancellationToken)
    {
        var user = _users.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled))
        {
            return Array.Empty<Candidate>();
        }

        // Complete a version/part association interrupted after its normal
        // library relationship became durable. Lookup remains a database and
        // metadata operation, including recovery of an older opaque link.
        foreach (var mapped in await _repository.ReadByAliasesAsync([identifier], cancellationToken).ConfigureAwait(false))
        {
            foreach (var itemId in await _repository.ReadItemIdsAsync(mapped.ContentId, cancellationToken).ConfigureAwait(false))
            {
                if (_library.GetItemById<BaseItem>(itemId, user) is Video video
                    && (video.PrimaryVersionId.HasValue || (!video.ExtraType.HasValue && !video.OwnerId.Equals(Guid.Empty))))
                {
                    await BindMetadataAsync(video, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var mappings = await _repository.ReadByAliasesAsync([identifier], cancellationToken).ConfigureAwait(false);
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
                var canRetainUnidentifiedIdentity = await CanRetainUnidentifiedIdentityAsync(
                    current,
                    metadata,
                    cancellationToken).ConfigureAwait(false);
                if (!Nullable.Equals(current.ContentId, mapping.ContentId) || metadata.Kind != mapping.Kind
                    || (IdentityChanged(current, metadata) && !canRetainUnidentifiedIdentity)
                    || (MediaContentIdentifier.ParseProviderAlias(identifier) is not null && !metadata.PublicAliases.Contains(identifier, StringComparer.Ordinal)))
                {
                    continue;
                }

                if (_library.GetItemById<BaseItem>(itemId, user) is not null)
                {
                    BaseItem physicalRecord = item;
                    if (item is Video video && !string.IsNullOrEmpty(video.Path))
                    {
                        var fileRecords = _library.GetMediaFileRecords(video.Path);
                        if (fileRecords.Count == 1)
                        {
                            physicalRecord = fileRecords[0];
                        }

                        if (!video.ExtraType.HasValue && !video.OwnerId.Equals(Guid.Empty)
                            && _library.GetItemById<Video>(video.OwnerId) is { } owner
                            && owner.AdditionalParts.Contains(video.Path, StringComparer.Ordinal))
                        {
                            physicalRecord = owner;
                        }
                    }

                    if (_library.GetItemById<BaseItem>(physicalRecord.Id, user) is not null
                        && !visible.Any(existing => existing.Id.Equals(physicalRecord.Id)))
                    {
                        physicalRecord.PermalinkContentId = mapping.ContentId;
                        visible.Add(physicalRecord);
                    }
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

        var existingBindings = await _repository.ReadByAliasesAsync([alias], token).ConfigureAwait(false);
        if (existingBindings.Count > 1)
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
        }).Where(value => !value.ExtraType.HasValue).ToArray();
        foreach (var item in items)
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

        foreach (var association in legacy.GroupBy(value => value.GroupId))
        {
            var providerAliases = association.Select(value => value.Alias)
                .Where(value => MediaContentIdentifier.ParseProviderAlias(value) is not null).Distinct(StringComparer.Ordinal).ToArray();
            var items = new Dictionary<Guid, BaseItem>();
            var originalId = association.First().ItemId;
            var original = originalId.HasValue ? _library.GetItemById<BaseItem>(originalId.Value, user) : null;
            if (original is not null && !original.ExtraType.HasValue)
            {
                var metadata = await ReadMetadataAsync(original, createSeriesIdentity: true, token).ConfigureAwait(false);
                if (providerAliases.Length == 0 || metadata.PublicAliases.Intersect(providerAliases, StringComparer.Ordinal).Any())
                {
                    items[original.Id] = original;
                }
            }

            var recordedPaths = association.Where(value => !string.IsNullOrEmpty(value.Path))
                .SelectMany(value => new[] { value.Path!, Path.ChangeExtension(value.Path!, ".mp4") })
                .Distinct(StringComparer.Ordinal);
            var legacyKind = association.First().ItemKind;
            foreach (var path in recordedPaths)
            {
                foreach (var item in _library.GetItemList(new InternalItemsQuery(user) { Recursive = true, Path = path }))
                {
                    if (item.ExtraType.HasValue || item is not (Video or Series or Season or MediaBrowser.Controller.Entities.Movies.BoxSet))
                    {
                        continue;
                    }

                    if (legacyKind is not null && !string.Equals(legacyKind, item.GetBaseItemKind().ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var metadata = await ReadMetadataAsync(item, createSeriesIdentity: true, token).ConfigureAwait(false);
                    if (providerAliases.Length == 0 || metadata.PublicAliases.Intersect(providerAliases, StringComparer.Ordinal).Any())
                    {
                        items[item.Id] = item;
                    }
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

            foreach (var associatedItem in items.Values)
            {
                var item = FindTitleRecord(associatedItem, out _) ?? associatedItem;
                var binding = await BindMetadataAsync(item, token).ConfigureAwait(false);
                if (MediaContentIdentifier.ParseProviderAlias(alias) is not null
                    && !(await ReadMetadataAsync(item, createSeriesIdentity: false, token).ConfigureAwait(false)).PublicAliases.Contains(alias, StringComparer.Ordinal))
                {
                    continue;
                }

                await _repository.ImportAliasAsync(
                    new PermalinkBindingRequest(
                        item.Id,
                        binding.ContentId,
                        binding.ContentId,
                        binding.Kind,
                        [alias],
                        null,
                        _clock.GetUtcNow().UtcDateTime),
                    token).ConfigureAwait(false);
            }
        }
    }

    private async Task<MediaPermalinkBinding> BindMetadataAsync(BaseItem item, CancellationToken token)
    {
        if (item.Id.Equals(Guid.Empty) || item.ExtraType.HasValue)
        {
            throw new PermalinkException(PermalinkErrorKind.Ineligible, "item-ineligible", "A persisted library title is required; extras and trailers do not have title permalinks.");
        }

        var primary = FindTitleRecord(item, out _);
        var primaryBinding = primary is null ? null : await BindMetadataAsync(primary, token).ConfigureAwait(false);

        var metadata = await ReadMetadataAsync(item, createSeriesIdentity: true, token).ConfigureAwait(false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var current = await _repository.ReadForItemAsync(item.Id, token).ConfigureAwait(false);
            var keys = metadata.PublicAliases.Concat(metadata.CoordinateKey is null ? Array.Empty<string>() : [metadata.CoordinateKey]).ToArray();
            var matches = await _repository.ReadByAliasesAsync(keys, token).ConfigureAwait(false);
            if (item.GetType() == typeof(Video) && primaryBinding is null && matches.Count == 1
                && matches[0].Kind != metadata.Kind && TryKnownVideoKind(matches[0].Kind, out var knownKind))
            {
                // Provider metadata can already identify a concrete title even
                // while its file record still has the generic Video CLR type.
                metadata = _identifiers.Identify(item, null, null, knownKind);
                keys = metadata.PublicAliases.ToArray();
                matches = await _repository.ReadByAliasesAsync(keys, token).ConfigureAwait(false);
            }

            if (matches.Any(value => !MediaContentIdentifier.IsKindRefinement(value.Kind, metadata.Kind)) || matches.Count > 1)
            {
                throw new PermalinkException(PermalinkErrorKind.Conflict, "metadata-identity-conflict", "The library metadata identifies more than one title. Correct its provider or episode metadata before associating this file.", itemId: item.Id);
            }

            var canRetainUnidentifiedIdentity = await CanRetainUnidentifiedIdentityAsync(
                current,
                metadata,
                token).ConfigureAwait(false);
            var contentId = primaryBinding?.ContentId ?? (matches.Count == 1 ? matches[0].ContentId
                : current.ContentId.HasValue && (!IdentityChanged(current, metadata) || canRetainUnidentifiedIdentity) ? current.ContentId.Value
                : _identifiers.CreateContentId());
            var inheritedContentId = current.ContentId.HasValue && !current.ContentId.Value.Equals(contentId)
                && MediaContentIdentifier.IsKindRefinement(current.Kind, metadata.Kind)
                && (current.Keys.All(key => key.StartsWith("sk-", StringComparison.Ordinal)) || canRetainUnidentifiedIdentity)
                ? current.ContentId : null;
            var aliases = metadata.PublicAliases.Append(MediaContentIdentifier.OpaqueAlias(contentId))
                .Concat(inheritedContentId.HasValue ? current.PublicAliases : Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            var target = matches.FirstOrDefault(value => value.ContentId.Equals(contentId));
            var retiredAliases = (target?.Keys ?? current.Keys).Where(alias => MediaContentIdentifier.IsReplacedClaim(alias, metadata)).ToArray();
            var legacyAliases = current.ContentId.HasValue ? Array.Empty<string>()
                : await ReadInitialLegacyAliasesAsync(item.Id, metadata, token).ConfigureAwait(false);
            if (primaryBinding is not null && current.ContentId.HasValue && !current.ContentId.Value.Equals(contentId)
                && MediaContentIdentifier.IsKindRefinement(current.Kind, metadata.Kind))
            {
                // Explicit version grouping is an existing library relationship.
                // Carry its previously issued opaque URLs in the same database
                // update, while keeping different provider claims separate.
                // If other files still use the source title, the old URL retains
                // both candidates instead of silently choosing one.
                legacyAliases = legacyAliases.Concat(current.PublicAliases.Where(alias => alias.StartsWith("sk-", StringComparison.Ordinal)))
                    .Distinct(StringComparer.Ordinal).ToArray();
            }

            if (current.ContentId.HasValue && current.ContentId.Value.Equals(contentId) && current.Kind == metadata.Kind
                && retiredAliases.Length == 0
                && aliases.All(alias => current.PublicAliases.Contains(alias, StringComparer.Ordinal))
                && (metadata.CoordinateKey is null || current.Keys.Contains(metadata.CoordinateKey, StringComparer.Ordinal)))
            {
                item.PermalinkContentId = contentId;
                return new MediaPermalinkBinding(contentId, metadata.Kind, current.PublicAliases, current.Keys);
            }

            try
            {
                await _repository.BindAsync(
                    new PermalinkBindingRequest(
                        item.Id,
                        current.ContentId,
                        contentId,
                        metadata.Kind,
                        aliases,
                        metadata.CoordinateKey,
                        _clock.GetUtcNow().UtcDateTime,
                        inheritedContentId,
                        legacyAliases,
                        retiredAliases),
                    token).ConfigureAwait(false);
                var saved = await _repository.ReadForItemAsync(item.Id, token).ConfigureAwait(false);
                if (!saved.ContentId.HasValue || !saved.ContentId.Value.Equals(contentId) || saved.PublicAliases.Count == 0)
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

    private async Task<string[]> ReadInitialLegacyAliasesAsync(Guid itemId, MediaContentIdentity metadata, CancellationToken token)
    {
        var aliases = await _legacy.ReadItemAliasesAsync(itemId, token).ConfigureAwait(false);
        var providerAliases = aliases.Where(alias => MediaContentIdentifier.ParseProviderAlias(alias) is not null).ToArray();
        if (metadata.PublicAliases.Count > 0 && providerAliases.Length > 0
            && !metadata.PublicAliases.Intersect(providerAliases, StringComparer.Ordinal).Any())
        {
            // A record explicitly reassigned to another title must not take
            // that earlier title's opaque URL with it during migration.
            return Array.Empty<string>();
        }

        return aliases.Where(alias => MediaContentIdentifier.IsPublicAlias(alias)
            && (alias.StartsWith("sk-", StringComparison.Ordinal) || metadata.PublicAliases.Contains(alias, StringComparer.Ordinal))).ToArray();
    }

    private async Task<MediaContentIdentity> ReadMetadataAsync(BaseItem item, bool createSeriesIdentity, CancellationToken token)
    {
        // Versions and stacked parts already have title relationships in the
        // library. Reuse their owner's metadata instead of identifying bytes.
        item = FindTitleRecord(item, out _) ?? item;
        var series = item switch
        {
            Episode episode when !episode.SeriesId.Equals(Guid.Empty) => _library.GetItemById<Series>(episode.SeriesId),
            Season season when !season.SeriesId.Equals(Guid.Empty) => _library.GetItemById<Series>(season.SeriesId),
            _ => null
        };
        Guid? seriesContentId = null;
        if (series is not null)
        {
            seriesContentId = createSeriesIdentity
                ? (await BindMetadataAsync(series, token).ConfigureAwait(false)).ContentId
                : (await _repository.ReadForItemAsync(series.Id, token).ConfigureAwait(false)).ContentId;
        }

        BaseItemKind? knownKind = null;
        if (item.GetType() == typeof(Video))
        {
            var stored = await _repository.ReadForItemAsync(item.Id, token).ConfigureAwait(false);
            if (TryKnownVideoKind(stored.Kind, out var kind))
            {
                knownKind = kind;
            }
        }

        return _identifiers.Identify(item, seriesContentId, series?.DisplayOrder, knownKind);
    }

    private static bool TryKnownVideoKind(string? value, out BaseItemKind kind) => Enum.TryParse(value, true, out kind)
        && kind is BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.MusicVideo;

    private Video? FindTitleRecord(BaseItem item, out bool isMultipart)
    {
        isMultipart = false;
        var visited = new HashSet<Guid> { item.Id };
        var current = item as Video;
        Video? title = null;
        while (current is not null)
        {
            Video? next;
            if (current.PrimaryVersionId is Guid primaryId)
            {
                next = _library.GetItemById<Video>(primaryId);
                if (next is null)
                {
                    // A surviving version keeps its stored identity after the
                    // primary was removed; normal library promotion can follow.
                    break;
                }

                if (!MediaContentIdentifier.IsKindRefinement(current.GetBaseItemKind().ToString(), next.GetBaseItemKind().ToString()))
                {
                    throw new PermalinkException(
                        PermalinkErrorKind.Conflict,
                        "version-kind-conflict",
                        "Alternate versions must belong to the same library media kind.",
                        itemId: item.Id);
                }
            }
            else if (!current.ExtraType.HasValue && !current.OwnerId.Equals(Guid.Empty)
                && _library.GetItemById<Video>(current.OwnerId) is Video owner
                && (owner.AdditionalParts.Contains(current.Path, StringComparer.Ordinal)
                    || string.Equals(owner.Path, current.Path, StringComparison.Ordinal)))
            {
                // OwnerId also covers extras. A stacked part or another
                // record for this exact file retains the owner's title.
                next = owner;
                isMultipart = true;
            }
            else
            {
                break;
            }

            if (!visited.Add(next.Id))
            {
                throw new PermalinkException(
                    PermalinkErrorKind.Conflict,
                    "version-link-cycle",
                    "The library's version or multipart relationship contains a cycle.",
                    itemId: item.Id);
            }

            current = next;
            title = current;
        }

        return title;
    }

    private static bool IdentityChanged(PermalinkItemBinding previous, MediaContentIdentity current)
    {
        if (previous.Kind is not null && !MediaContentIdentifier.IsKindRefinement(previous.Kind, current.Kind))
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

    private Task<bool> CanRetainUnidentifiedIdentityAsync(
        PermalinkItemBinding current,
        MediaContentIdentity metadata,
        CancellationToken cancellationToken)
    {
        if (!current.ContentId.HasValue || current.Kind != metadata.Kind || metadata.CoordinateKey is null
            || !current.Keys.Any(key => MediaContentIdentifier.TryGetChangedCoordinateParents(
                key,
                metadata.CoordinateKey,
                out _,
                out _)))
        {
            return Task.FromResult(false);
        }

        return _repository.CanRetainUnidentifiedIdentityAsync(
            current.ContentId,
            metadata.Kind,
            metadata.CoordinateKey,
            cancellationToken);
    }

    private static PermalinkException MappingChanged() => new(
        PermalinkErrorKind.Conflict,
        "mapping-changed",
        "The media metadata changed while its permalink association was being saved.");

    /// <summary>A logical title and its current visible library versions.</summary>
    public sealed record Candidate(Guid ContentId, string Kind, IReadOnlyList<BaseItem> Items);
}
