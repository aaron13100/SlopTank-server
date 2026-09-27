// SlopTank modification notice: added or changed by SlopTank on 2026-09-27.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

// allow-no-test-found: exercised through private PermalinkRemovalControllerTests.cs

/// <summary>Defines the closed evidence protocol and equivalent-media identity policy.</summary>
internal static class PermalinkRemovalPolicy
{
    private const double FrameThreshold = 35D;
    private const int MaximumSearchOffsetMagnitude = 86400;
    private static readonly int[] _sampleTimestamps = [400, 800];
    private static readonly int[] _requiredSearchOffsets = [-12, -8, -4, 0, 4, 8, 12];

    /// <summary>Validates the complete caller-supplied removal proof shape.</summary>
    /// <param name="source">The source library item.</param>
    /// <param name="request">The requested removal.</param>
    public static void ValidateInput(BaseItem source, PermalinkRemovalPrepareRequest request)
    {
        var proof = request.EquivalenceEvidence;
        if (request.OperationId.Equals(Guid.Empty)
            || !request.ItemId.Equals(source.Id)
            || request.SurvivorItemId.Equals(source.Id)
            || proof is null
            || !proof.SourceItemId.Equals(source.Id)
            || !proof.SurvivorItemId.Equals(request.SurvivorItemId)
            || string.IsNullOrWhiteSpace(source.Path)
            || string.IsNullOrWhiteSpace(request.DestinationPath)
            || !string.Equals(proof.SourcePath, source.Path, StringComparison.Ordinal)
            || !string.Equals(proof.DestinationPath, request.DestinationPath, StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(request.DestinationPath)
            || string.Equals(source.Path, request.DestinationPath, StringComparison.Ordinal)
            || !double.IsFinite(proof.Threshold)
            || proof.Threshold != FrameThreshold
            || proof.Samples is null)
        {
            throw Conflict("removal-input", "Removal input or equivalence evidence is invalid.");
        }

        if (request.SurvivorItemId is null)
        {
            if (!string.IsNullOrEmpty(proof.SurvivorPath) || proof.Samples.Count != 0)
            {
                throw Conflict(
                    "removal-frame-evidence",
                    "A terminal retirement must not claim equivalence to an absent survivor.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(proof.SurvivorPath)
            || !Path.IsPathFullyQualified(proof.SurvivorPath)
            || proof.Samples.Count != _sampleTimestamps.Length)
        {
            throw Conflict("removal-frame-evidence", "Equivalent-survivor evidence is incomplete.");
        }

        IReadOnlyList<int>? firstOffsets = null;
        for (var index = 0; index < proof.Samples.Count; index++)
        {
            var sample = proof.Samples[index];
            if (sample is null
                || sample.SearchOffsetsSeconds is null
                || sample.TimestampSeconds != _sampleTimestamps[index]
                || sample.SearchOffsetsSeconds.Count < _requiredSearchOffsets.Length
                || sample.SearchOffsetsSeconds.Count > _requiredSearchOffsets.Length + 3
                || !sample.SearchOffsetsSeconds.SequenceEqual(
                    sample.SearchOffsetsSeconds.OrderBy(value => value))
                || sample.SearchOffsetsSeconds.Distinct().Count() != sample.SearchOffsetsSeconds.Count
                || sample.SearchOffsetsSeconds.Any(value => Math.Abs((long)value) > MaximumSearchOffsetMagnitude)
                || _requiredSearchOffsets.Any(value => !sample.SearchOffsetsSeconds.Contains(value))
                || (firstOffsets is not null && !sample.SearchOffsetsSeconds.SequenceEqual(firstOffsets))
                || !double.IsFinite(sample.DecodedFrameDifference)
                || sample.DecodedFrameDifference < 0D
                || sample.DecodedFrameDifference > FrameThreshold)
            {
                throw Conflict(
                    "removal-frame-evidence",
                    "Decoded-frame evidence does not match the conservative proof protocol.");
            }

            firstOffsets ??= sample.SearchOffsetsSeconds;
        }
    }

    /// <summary>Returns the non-empty authoritative provider identity.</summary>
    /// <param name="identityOwner">The movie or episode's series.</param>
    /// <returns>The normalized provider identity.</returns>
    public static IReadOnlyDictionary<string, string> RequireProviderIdentity(BaseItem identityOwner)
    {
        var identity = identityOwner.ProviderIds
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return identity.Count > 0
            ? identity
            : throw Conflict("removal-provider-identity", "Source has no authoritative provider identity.");
    }

    /// <summary>Determines whether two items have the same durable media identity.</summary>
    /// <param name="source">The frozen source identity.</param>
    /// <param name="sourceProviderIdentity">The source provider identity.</param>
    /// <param name="survivor">The proposed survivor.</param>
    /// <param name="survivorProviderIdentity">The survivor provider identity.</param>
    /// <returns>Whether the survivor is authoritative for the source.</returns>
    public static bool IsEquivalentIdentity(
        PermalinkIdentitySnapshot source,
        IReadOnlyDictionary<string, string> sourceProviderIdentity,
        BaseItem survivor,
        IReadOnlyDictionary<string, string> survivorProviderIdentity)
    {
        if (source.ItemKind == nameof(Episode) && survivor is Episode episode)
        {
            return source.ParentIndexNumber == episode.ParentIndexNumber
                && source.IndexNumber == episode.IndexNumber
                && source.IndexNumberEnd == episode.IndexNumberEnd
                && ProviderIdentityEquals(sourceProviderIdentity, survivorProviderIdentity);
        }

        return source.ItemKind == nameof(Movie)
            && survivor is Movie
            && ProviderIdentityEquals(sourceProviderIdentity, survivorProviderIdentity);
    }

    private static bool ProviderIdentityEquals(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        return expected.Count == actual.Count
            && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
                && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}
