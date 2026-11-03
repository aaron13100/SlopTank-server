// SlopTank modification notice: added or changed by SlopTank on 2026-11-02, 2026-11-03.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// <param name="knownKind">An existing logical title classification for a generic video record.</param>
    /// <returns>Logical identifiers; an unidentified record can receive a persistent opaque ID.</returns>
    internal MediaContentIdentity Identify(BaseItem item, Guid? seriesContentId, string? seriesOrder, BaseItemKind? knownKind = null)
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
            Video when knownKind == BaseItemKind.Movie => ("movie", "mv"),
            Video when knownKind == BaseItemKind.Episode => ("episode", "ep"),
            Video when knownKind == BaseItemKind.MusicVideo => ("musicvideo", (string?)null),
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

    /// <summary>Recognizes a concrete classification of an existing generic video title.</summary>
    /// <param name="previous">The stored content classification.</param>
    /// <param name="current">The classification from the current library record.</param>
    /// <returns>Whether the classification is unchanged or refines a generic video to a supported content kind.</returns>
    internal static bool IsKindRefinement(string? previous, string current) => string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)
        || (string.Equals(previous, "video", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(current, "movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, "episode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, "musicvideo", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Recognizes an unchanged TV coordinate under a changed parent content ID.</summary>
    /// <param name="previous">The stored private coordinate.</param>
    /// <param name="current">The coordinate computed from current metadata.</param>
    /// <param name="previousParentId">The parent ID embedded in the stored coordinate.</param>
    /// <param name="currentParentId">The parent ID embedded in the current coordinate.</param>
    /// <returns>Whether only the nonempty parent IDs differ and the encoded coordinate suffix is identical.</returns>
    internal static bool TryGetChangedCoordinateParents(
        string? previous,
        string? current,
        out Guid previousParentId,
        out Guid currentParentId)
    {
        previousParentId = Guid.Empty;
        currentParentId = Guid.Empty;
        return TryParseCoordinate(previous, out previousParentId, out var previousSuffix)
            && TryParseCoordinate(current, out currentParentId, out var currentSuffix)
            && !previousParentId.Equals(Guid.Empty)
            && !currentParentId.Equals(Guid.Empty)
            && !previousParentId.Equals(currentParentId)
            && string.Equals(previousSuffix, currentSuffix, StringComparison.Ordinal);
    }

    /// <summary>Identifies superseded metadata claims while preserving opaque logical IDs.</summary>
    /// <param name="alias">The previously stored alias.</param>
    /// <param name="metadata">The current metadata-derived identity.</param>
    /// <returns>Whether the alias represents a claim replaced by the current metadata.</returns>
    internal static bool IsReplacedClaim(string alias, MediaContentIdentity metadata)
    {
        if (alias.StartsWith("metadata:v1:", StringComparison.Ordinal))
        {
            return metadata.CoordinateKey is not null && alias != metadata.CoordinateKey;
        }

        var previous = ParseProviderAlias(alias);
        return previous is not null && metadata.PublicAliases.Any(value =>
        {
            var current = ParseProviderAlias(value);
            return current is not null && current.Provider == previous.Provider && current.Kind == previous.Kind && current.Value != previous.Value;
        });
    }

    /// <summary>Translates an existing provider URL into the library's metadata query.</summary>
    /// <param name="alias">The public provider or opaque alias.</param>
    /// <returns>The provider identity represented by the alias, or <see langword="null"/> for an opaque alias.</returns>
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

    private static bool TryParseCoordinate(string? value, out Guid parentContentId, out string suffix)
    {
        const string prefix = "metadata:v1:";
        parentContentId = Guid.Empty;
        suffix = string.Empty;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parentEnd = value.IndexOf(':', prefix.Length);
        if (parentEnd != prefix.Length + 32
            || !Guid.TryParseExact(value[prefix.Length..parentEnd], "N", out parentContentId)
            || parentContentId.Equals(Guid.Empty))
        {
            return false;
        }

        suffix = value[(parentEnd + 1)..];
        // The current coordinate comes from CoordinateKey above. Exact suffix
        // equality also retains its order, media kind and episode numbers.
        return suffix.Length > 0;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed record ProviderIdentity(string Provider, string Value, BaseItemKind? Kind);
}
