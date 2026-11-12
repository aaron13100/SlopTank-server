// SlopTank modification notice: added or changed by SlopTank on 2026-11-12.
using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Applies an operation to current user data without replaying unrelated fields from an older snapshot.
/// </summary>
internal static class UserDataUpdatePolicy
{
    private static readonly IReadOnlyDictionary<UserDataSaveReason, Action<UserData, UserItemData>> _snapshotUpdates =
        new Dictionary<UserDataSaveReason, Action<UserData, UserItemData>>
        {
            [UserDataSaveReason.PlaybackStart] = ApplyPlaybackStart,
            [UserDataSaveReason.PlaybackProgress] = ApplyPlaybackProgress,
            [UserDataSaveReason.PlaybackFinished] = ApplyPlaybackFinished,
            [UserDataSaveReason.TogglePlayed] = ApplyPlayedState,
            [UserDataSaveReason.UpdateUserRating] = ApplyRating,
            [UserDataSaveReason.ResumeCleared] = ApplyResumeCleared,
            [UserDataSaveReason.Import] = ApplyWholeRecord,
            [UserDataSaveReason.UpdateUserData] = ApplyWholeRecord,
        };

    /// <summary>Apply only the fields owned by this snapshot's operation.</summary>
    internal static void ApplySnapshot(UserData current, UserItemData incoming, UserDataSaveReason reason)
    {
        if (!_snapshotUpdates.TryGetValue(reason, out var update))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "No user-data update policy exists for this save reason.");
        }

        update(current, incoming);
    }

    /// <summary>Apply the fields explicitly supplied by a partial API request.</summary>
    internal static void ApplyPatch(UserData current, UpdateUserItemDataDto incoming)
    {
        if (incoming.PlaybackPositionTicks.HasValue)
        {
            current.PlaybackPositionTicks = incoming.PlaybackPositionTicks.Value;
        }

        if (incoming.PlayCount.HasValue)
        {
            current.PlayCount = incoming.PlayCount.Value;
        }

        if (incoming.IsFavorite.HasValue)
        {
            current.IsFavorite = incoming.IsFavorite.Value;
        }

        if (incoming.Likes.HasValue && !incoming.ClearRating)
        {
            current.Likes = incoming.Likes.Value;
            current.Rating = incoming.Likes.Value ? 10 : 1;
        }

        if (incoming.Played.HasValue)
        {
            current.Played = incoming.Played.Value;
        }

        if (incoming.LastPlayedDate.HasValue)
        {
            current.LastPlayedDate = incoming.LastPlayedDate.Value;
        }

        if (incoming.Rating.HasValue && !incoming.ClearRating)
        {
            // Retain the domain object's existing range validation and rating/like conversion.
            var rating = new UserItemData { Key = current.CustomDataKey, Rating = incoming.Rating.Value };
            current.Rating = rating.Rating;
            current.Likes = rating.Likes;
        }

        if (incoming.ClearRating)
        {
            current.Rating = null;
            current.Likes = null;
        }
    }

    private static void ApplyPlaybackStart(UserData current, UserItemData incoming)
    {
        // Increment the persisted count, not the count read before another request committed.
        current.PlayCount++;
        KeepNewestPlaybackDate(current, incoming);
        current.Played |= incoming.Played;
    }

    private static void ApplyPlaybackProgress(UserData current, UserItemData incoming)
    {
        ApplyPlaybackPosition(current, incoming);
        current.AudioStreamIndex = incoming.AudioStreamIndex;
        current.SubtitleStreamIndex = incoming.SubtitleStreamIndex;
    }

    private static void ApplyPlaybackFinished(UserData current, UserItemData incoming)
    {
        ApplyPlaybackPosition(current, incoming);
        // A stop without a reported position historically increments the caller's count.
        current.PlayCount = Math.Max(current.PlayCount, incoming.PlayCount);
        KeepNewestPlaybackDate(current, incoming);
    }

    private static void ApplyPlaybackPosition(UserData current, UserItemData incoming)
    {
        current.PlaybackPositionTicks = incoming.PlaybackPositionTicks;
        // Playback may mark completion. Clearing watched state is an explicit TogglePlayed operation.
        current.Played |= incoming.Played;
    }

    private static void ApplyPlayedState(UserData current, UserItemData incoming)
    {
        current.PlayCount = incoming.PlayCount;
        current.LastPlayedDate = incoming.LastPlayedDate;
        current.Played = incoming.Played;
        current.PlaybackPositionTicks = incoming.PlaybackPositionTicks;
    }

    private static void ApplyRating(UserData current, UserItemData incoming)
    {
        current.IsFavorite = incoming.IsFavorite;
        current.Rating = incoming.Rating;
        current.Likes = incoming.Likes;
    }

    private static void ApplyResumeCleared(UserData current, UserItemData incoming)
        => current.PlaybackPositionTicks = incoming.PlaybackPositionTicks;

    private static void ApplyWholeRecord(UserData current, UserItemData incoming)
    {
        // Import and explicit whole-record updates retain their authoritative replacement contract.
        ApplyPlayedState(current, incoming);
        ApplyRating(current, incoming);
        current.AudioStreamIndex = incoming.AudioStreamIndex;
        current.SubtitleStreamIndex = incoming.SubtitleStreamIndex;
    }

    private static void KeepNewestPlaybackDate(UserData current, UserItemData incoming)
    {
        if (incoming.LastPlayedDate.HasValue
            && (!current.LastPlayedDate.HasValue || incoming.LastPlayedDate.Value > current.LastPlayedDate.Value))
        {
            current.LastPlayedDate = incoming.LastPlayedDate;
        }
    }
}
