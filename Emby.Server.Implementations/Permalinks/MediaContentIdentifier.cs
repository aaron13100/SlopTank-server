// SlopTank modification notice: added or changed by SlopTank on 2026-11-02.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Adapts existing library metadata to logical content identifiers. It never
/// opens a media file or derives identity from its bytes, pathname or native ID.
/// </summary>
public sealed class MediaContentIdentifier
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private static readonly (string Provider, string Prefix)[] NumericProviders = [("Tmdb", "tm"), ("Tvdb", "tv")];

    /// <summary>Describes a title using provider metadata and, for TV, its parent title.</summary>
    /// <param name="item">The existing library metadata record.</param>
    /// <param name="seriesContentId">The persistent logical series ID, when available.</param>
    /// <param name="seriesOrder">The series' metadata episode ordering.</param>
    /// <returns>Logical identifiers; an unidentified record can receive a persistent opaque ID.</returns>
    internal MediaContentIdentity Identify(BaseItem item, Guid? seriesContentId, string? seriesOrder)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (kind, qualifier) = item switch
        {
            Movie => ("movie", "mv"),
            Series => ("series", "tv"),
            Season => ("season", "se"),
            Episode => ("episode", "ep"),
            BoxSet => ("boxset", "co"),
            MusicVideo => ("musicvideo", (string?)null),
            Video => ("video", (string?)null),
            _ => throw new PermalinkException(PermalinkErrorKind.Ineligible, "item-ineligible", "This library item has no supported permalink content kind.")
        };

        var aliases = new List<string>(3);
        if (item.ProviderIds.TryGetValue("Imdb", out var imdb)
            && imdb.Length > 2 && imdb.StartsWith("tt", StringComparison.Ordinal)
            && imdb.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            aliases.Add(imdb);
        }

        if (qualifier is not null)
        {
            AddNumericAliases(item, qualifier, aliases);
        }

        var coordinate = CoordinateKey(item, seriesContentId, seriesOrder);
        return new MediaContentIdentity(kind, aliases.AsReadOnly(), coordinate);
    }

    /// <summary>Allocates a content ID once when the database has no existing identification.</summary>
    /// <returns>A new logical title ID, independent of any media file.</returns>
    internal Guid CreateContentId() => Guid.NewGuid();

    /// <summary>Translates an existing provider URL into the library's metadata query.</summary>
    internal static ProviderIdentity? ParseProviderAlias(string alias)
    {
        if (!IsPublicAlias(alias) || alias.StartsWith("sk-", StringComparison.Ordinal))
        {
            return null;
        }

        if (alias.StartsWith("tt", StringComparison.Ordinal))
        {
            return new ProviderIdentity("Imdb", alias, null);
        }

        var parts = alias.Split('-');
        var kind = parts[1] switch
        {
            "mv" => BaseItemKind.Movie,
            "tv" => BaseItemKind.Series,
            "se" => BaseItemKind.Season,
            "ep" => BaseItemKind.Episode,
            "co" => BaseItemKind.BoxSet,
            _ => throw new PermalinkException(PermalinkErrorKind.Ineligible, "identifier-kind-invalid", "The provider identifier has an unsupported media kind.")
        };
        return new ProviderIdentity(parts[0] == "tm" ? "Tmdb" : "Tvdb", parts[2], kind);
    }

    /// <summary>Recognizes the existing public URL namespaces without accessing media.</summary>
    /// <param name="alias">An untrusted URL identifier.</param>
    /// <returns>Whether the identifier has the supported public grammar.</returns>
    internal static bool IsPublicAlias(string alias) => alias is { Length: > 0 and <= 512 }
        && Regex.IsMatch(alias, "^(?:tt[0-9]+|(?:tm|tv)-(?:mv|tv|ep|se|co)-[1-9][0-9]*|sk-[0-9a-hjkmnp-tv-z]{26})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Encodes a logical title ID using the existing public sk URL grammar.</summary>
    /// <param name="contentId">The persisted logical title ID.</param>
    /// <returns>A portable opaque permalink alias.</returns>
    internal static string OpaqueAlias(Guid contentId)
    {
        var value = new BigInteger(contentId.ToByteArray(), isUnsigned: true, isBigEndian: true);
        Span<char> encoded = stackalloc char[26];
        for (var index = encoded.Length - 1; index >= 0; index--)
        {
            encoded[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return "sk-" + new string(encoded);
    }

    private static void AddNumericAliases(BaseItem item, string qualifier, List<string> aliases)
    {
        foreach (var provider in NumericProviders)
        {
            if (item.ProviderIds.TryGetValue(provider.Provider, out var id)
                && id.Length > 0 && id[0] != '0'
                && id.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0)
            {
                aliases.Add($"{provider.Prefix}-{qualifier}-{id}");
            }
        }
    }

    private static string? CoordinateKey(BaseItem item, Guid? seriesContentId, string? seriesOrder)
    {
        if (!seriesContentId.HasValue)
        {
            return null;
        }

        var order = Uri.EscapeDataString(seriesOrder ?? string.Empty);
        var prefix = $"metadata:v1:{seriesContentId.Value:N}:{order}";
        return item switch
        {
            Season { IndexNumber: >= 0 } season => $"{prefix}:season:{Number(season.IndexNumber!.Value)}",
            Episode { ParentIndexNumber: >= 0, IndexNumber: >= 0 } episode =>
                $"{prefix}:episode:{Number(episode.ParentIndexNumber!.Value)}:{Number(episode.IndexNumber!.Value)}:{Number(episode.IndexNumberEnd ?? episode.IndexNumber.Value)}",
            _ => null
        };
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed record ProviderIdentity(string Provider, string Value, BaseItemKind? Kind);
}
