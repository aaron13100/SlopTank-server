// SlopTank modification notice: added or changed by SlopTank on 2026-09-23.
#pragma warning disable RS0030 // Do not use banned APIs
#pragma warning disable CA1304 // Specify CultureInfo
#pragma warning disable CA1311 // Specify a culture or use an invariant version
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

using System;
using System.Linq;
using System.Linq.Expressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Static class for methods which maps types of ordering to their respecting ordering functions.
/// </summary>
public static class OrderMapper
{
    /// <summary>
    /// Creates Func to be executed later with a given BaseItemEntity input for sorting items on query.
    /// </summary>
    /// <param name="sortBy">Item property to sort by.</param>
    /// <param name="query">Context Query.</param>
    /// <param name="jellyfinDbContext">Context.</param>
    /// <returns>Func to be executed later for sorting query.</returns>
    public static Expression<Func<BaseItemEntity, object?>> MapOrderByField(ItemSortBy sortBy, InternalItemsQuery query, JellyfinDbContext jellyfinDbContext)
    {
        return (sortBy, query.User) switch
        {
            (ItemSortBy.AirTime, _) => e => e.SortName,
            (ItemSortBy.Runtime, _) => e => e.RunTimeTicks,
            (ItemSortBy.Random, _) => e => EF.Functions.Random(),
            (ItemSortBy.DatePlayed, not null) => e =>
                jellyfinDbContext.UserData
                    .Where(w => w.UserId == query.User.Id && (w.ItemId == e.Id || w.Item!.PrimaryVersionId == e.Id))
                    .Max(f => f.LastPlayedDate),
            (ItemSortBy.DatePlayed, null) => e =>
                jellyfinDbContext.UserData
                    .Where(w => w.ItemId == e.Id || w.Item!.PrimaryVersionId == e.Id)
                    .Max(f => f.LastPlayedDate),
            (ItemSortBy.PlayCount, _) => e => e.UserData!.Where(f => f.UserId.Equals(query.User!.Id)).OrderBy(f => f.CustomDataKey).FirstOrDefault()!.PlayCount,
            (ItemSortBy.IsFavoriteOrLiked, _) => e => e.UserData!.Where(f => f.UserId.Equals(query.User!.Id)).OrderBy(f => f.CustomDataKey).Select(f => (bool?)f.IsFavorite).FirstOrDefault() ?? false,
            (ItemSortBy.IsFolder, _) => e => e.IsFolder,
            (ItemSortBy.IsPlayed, _) => e => e.UserData!.Where(f => f.UserId.Equals(query.User!.Id)).OrderBy(f => f.CustomDataKey).FirstOrDefault()!.Played,
            (ItemSortBy.IsUnplayed, _) => e => !e.UserData!.Where(f => f.UserId.Equals(query.User!.Id)).OrderBy(f => f.CustomDataKey).FirstOrDefault()!.Played,
            (ItemSortBy.DateLastContentAdded, _) => e => e.DateLastMediaAdded,
            (ItemSortBy.Artist, _) => e => e.ItemValues!.Where(f => f.ItemValue.Type == ItemValueType.Artist).OrderBy(f => f.ItemValue.CleanValue).Select(f => f.ItemValue.CleanValue).FirstOrDefault(),
            (ItemSortBy.AlbumArtist, _) => e => e.ItemValues!.Where(f => f.ItemValue.Type == ItemValueType.AlbumArtist).OrderBy(f => f.ItemValue.CleanValue).Select(f => f.ItemValue.CleanValue).FirstOrDefault(),
            (ItemSortBy.Studio, _) => e => e.ItemValues!.Where(f => f.ItemValue.Type == ItemValueType.Studios).OrderBy(f => f.ItemValue.CleanValue).Select(f => f.ItemValue.CleanValue).FirstOrDefault(),
            (ItemSortBy.OfficialRating, _) => e => e.InheritedParentalRatingValue,
            (ItemSortBy.SeriesSortName, _) => e => e.SeriesName,
            (ItemSortBy.Album, _) => e => e.Album,
            (ItemSortBy.DateCreated, _) => e => e.DateCreated,
            (ItemSortBy.PremiereDate, _) => e => e.PremiereDate ?? (e.ProductionYear.HasValue ? DateTime.MinValue.AddYears(e.ProductionYear.Value - 1) : null),
            (ItemSortBy.StartDate, _) => e => e.StartDate,
            (ItemSortBy.Name, _) => e => e.SortName,
            (ItemSortBy.CommunityRating, _) => e => e.CommunityRating,
            (ItemSortBy.ProductionYear, _) => e => e.ProductionYear,
            (ItemSortBy.CriticRating, _) => e => e.CriticRating,
            (ItemSortBy.VideoBitRate, _) => e => e.TotalBitrate,
            (ItemSortBy.ParentIndexNumber, _) => e => e.ParentIndexNumber,
            (ItemSortBy.IndexNumber, _) => e => e.IndexNumber,
            // SeriesDatePlayed is normally handled via pre-aggregated join in ApplySeriesDatePlayedOrder.
            // This correlated subquery fallback is only reached when combined with search.
            (ItemSortBy.SeriesDatePlayed, not null) => e =>
                jellyfinDbContext.UserData
                    .Where(w => w.UserId == query.User.Id && w.Played && w.Item!.SeriesPresentationUniqueKey == e.PresentationUniqueKey)
                    .Max(f => f.LastPlayedDate),
            (ItemSortBy.SeriesDatePlayed, null) => e =>
                jellyfinDbContext.UserData
                    .Where(w => w.Played && w.Item!.SeriesPresentationUniqueKey == e.PresentationUniqueKey)
                    .Max(f => f.LastPlayedDate),
            _ => e => e.SortName
        };
    }

    /// <summary>
    /// Creates an expression to order search results by match quality.
    /// Prioritizes: exact match (0) > prefix match with word boundary (1) > prefix match (2) >
    /// contains (3) on the ordinary (punctuation/whitespace-collapsed) clean value, all ranked
    /// strictly ahead of a match found only once whitespace inside the clean value is removed
    /// entirely (4) -- the looser, whitespace/punctuation-insensitive tier that
    /// BaseItemRepository.TranslateQuery's SearchTerm predicate also matches on.
    /// Considers both CleanName and OriginalTitle for matching.
    /// </summary>
    /// <param name="searchTerm">The search term to match against.</param>
    /// <returns>An expression that returns an integer representing match quality (lower is better).</returns>
    public static Expression<Func<BaseItemEntity, int>> MapSearchRelevanceOrder(string searchTerm)
    {
        // Use the same clean-value normalization the SearchTerm WHERE predicate and the stored
        // CleanName column use (StringExtensions.GetCleanValue), instead of a second, divergent
        // scheme: a duplicate here that only lowercased and stripped diacritics never matched
        // CleanName for a punctuated or irregularly-spaced query, so it could never reach the
        // exact/prefix buckets below for those titles.
        var cleanSearchTerm = searchTerm.GetCleanValue();
        var searchPrefix = cleanSearchTerm + " ";
        var originalSearchLower = searchTerm.ToLowerInvariant();
        var originalSearchPrefix = originalSearchLower + " ";
        return e =>
            // Exact match on CleanName or OriginalTitle
            (e.CleanName == cleanSearchTerm || (e.OriginalTitle != null && e.OriginalTitle.ToLower() == originalSearchLower)) ? 0 :
            // Prefix match with word boundary
            (e.CleanName!.StartsWith(searchPrefix) || (e.OriginalTitle != null && e.OriginalTitle.ToLower().StartsWith(originalSearchPrefix))) ? 1 :
            // Prefix match
            (e.CleanName!.StartsWith(cleanSearchTerm) || (e.OriginalTitle != null && e.OriginalTitle.ToLower().StartsWith(originalSearchLower))) ? 2 :
            // Contains match on the ordinary clean value
            (e.CleanName!.Contains(cleanSearchTerm) || (e.OriginalTitle != null && e.OriginalTitle.ToLower().Contains(originalSearchLower))) ? 3 :
            // Anything else that reached ORDER BY only matched the looser, whitespace-removed tier.
            4;
    }
}
