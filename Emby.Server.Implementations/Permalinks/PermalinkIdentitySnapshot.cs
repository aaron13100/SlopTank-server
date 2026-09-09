// SlopTank modification notice: added or changed by SlopTank on 2026-07-29, 2026-09-09.
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Freezes all identity-bearing item fields for guarded logical recovery.
/// </summary>
/// <param name="ProviderIds">The complete provider dictionary.</param>
/// <param name="ItemKind">The immutable item kind.</param>
/// <param name="Name">The item name.</param>
/// <param name="OriginalTitle">The original title.</param>
/// <param name="ProductionYear">The production year.</param>
/// <param name="PremiereDate">The premiere date.</param>
/// <param name="SeriesName">The series name.</param>
/// <param name="ParentIndexNumber">The parent index number.</param>
/// <param name="IndexNumber">The item index number.</param>
/// <param name="IndexNumberEnd">The optional episode range end.</param>
internal sealed record PermalinkIdentitySnapshot(
    IReadOnlyDictionary<string, string> ProviderIds,
    string ItemKind,
    string? Name,
    string? OriginalTitle,
    int? ProductionYear,
    DateTime? PremiereDate,
    string? SeriesName,
    int? ParentIndexNumber,
    int? IndexNumber,
    int? IndexNumberEnd)
{
    /// <summary>
    /// Captures the complete identity-bearing state of an item.
    /// </summary>
    /// <param name="item">The item to capture.</param>
    /// <returns>The immutable identity snapshot.</returns>
    public static PermalinkIdentitySnapshot Capture(BaseItem item)
    {
        return new PermalinkIdentitySnapshot(
            new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
            item.GetType().Name,
            item.Name,
            item.OriginalTitle,
            item.ProductionYear,
            item.PremiereDate,
            item is IHasSeries hasSeries ? hasSeries.SeriesName : null,
            item.ParentIndexNumber,
            item.IndexNumber,
            item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? episode.IndexNumberEnd
                : null);
    }

    /// <summary>
    /// Tests whether an item exactly matches the captured identity.
    /// </summary>
    /// <param name="item">The item to compare.</param>
    /// <returns><see langword="true"/> when every identity-bearing field matches.</returns>
    public bool Matches(BaseItem item)
    {
        return string.Equals(ItemKind, item.GetType().Name, StringComparison.Ordinal)
            && string.Equals(Name, item.Name, StringComparison.Ordinal)
            && string.Equals(OriginalTitle, item.OriginalTitle, StringComparison.Ordinal)
            && ProductionYear == item.ProductionYear
            && PremiereDate == item.PremiereDate
            && string.Equals(
                SeriesName,
                item is IHasSeries hasSeries ? hasSeries.SeriesName : null,
                StringComparison.Ordinal)
            && ParentIndexNumber == item.ParentIndexNumber
            && IndexNumber == item.IndexNumber
            && IndexNumberEnd == (item is MediaBrowser.Controller.Entities.TV.Episode episode
                ? episode.IndexNumberEnd
                : null)
            && ProviderIds.Count == item.ProviderIds.Count
            && ProviderIds.All(pair => item.ProviderIds.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    /// <summary>
    /// Restores the exact captured identity-bearing fields.
    /// </summary>
    /// <param name="item">The item to restore.</param>
    public void Restore(BaseItem item)
    {
        item.ProviderIds = new Dictionary<string, string>(ProviderIds, StringComparer.OrdinalIgnoreCase);
        item.Name = Name;
        item.OriginalTitle = OriginalTitle;
        item.ProductionYear = ProductionYear;
        item.PremiereDate = PremiereDate;
        item.ParentIndexNumber = ParentIndexNumber;
        item.IndexNumber = IndexNumber;
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            episode.IndexNumberEnd = IndexNumberEnd;
        }

        if (item is IHasSeries hasSeries)
        {
            hasSeries.SeriesName = SeriesName;
        }
    }
}
