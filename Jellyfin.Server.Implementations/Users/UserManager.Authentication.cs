// SlopTank modification notice: added or changed by SlopTank on 2026-10-12.
#pragma warning disable RS0030 // Do not use banned APIs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users;

public partial class UserManager
{
    private async Task<User?> AuthenticateKnownUserAsync(
        Guid userId,
        string username,
        string password,
        string remoteEndPoint,
        bool isUserSession)
    {
        User resolvedUser;
        List<IAuthenticationProvider> providers;
        string? startingProviderId;
        AuthenticationOutcome? builtInOutcome = null;
        PasswordHashUpgrade? passwordHashUpgrade = null;

        using (await _userLock.LockAsync(userId).ConfigureAwait(false))
        {
            using (var dbContext = _dbProvider.CreateDbContext())
            {
                resolvedUser = await UserQuery(dbContext)
                    .FirstOrDefaultAsync(user => user.Id.Equals(userId))
                    .ConfigureAwait(false)
                    ?? throw new AuthenticationException("The account no longer exists.");
                startingProviderId = resolvedUser.AuthenticationProviderId;
                providers = GetAuthenticationProviders(resolvedUser);

                if (providers.Count > 0 && IsRegisteredBuiltInAuthenticationProvider(providers[0]))
                {
                    string? oldPassword = resolvedUser.Password;
                    var result = await AuthenticateWithProvider(providers[0], username, password, resolvedUser).ConfigureAwait(false);
                    if (result.Success)
                    {
                        passwordHashUpgrade = CreatePasswordHashUpgrade(providers[0], resolvedUser, oldPassword);
                        builtInOutcome = new AuthenticationOutcome(providers[0], result.Username, true);
                    }
                    else if (providers.Count == 1)
                    {
                        builtInOutcome = new AuthenticationOutcome(providers[0], result.Username, false);
                    }
                    else
                    {
                        providers = providers.Skip(1).ToList();
                    }
                }
            }

            if (builtInOutcome is not null)
            {
                return await CompleteAuthenticationUnderLockAsync(
                    userId,
                    builtInOutcome,
                    remoteEndPoint,
                    isUserSession,
                    startingProviderId,
                    providerCreatedUser: false,
                    newUserPolicy: null,
                    passwordHashUpgrade: passwordHashUpgrade,
                    expectedHandoff: null).ConfigureAwait(false);
            }
        }

        AuthenticationOutcome outcome = await AuthenticateWithProvidersAsync(providers, username, password, resolvedUser).ConfigureAwait(false);
        AuthenticationHandoffSnapshot handoff = await CaptureHandoffByIdAsync(userId, outcome.AuthenticationProvider, null).ConfigureAwait(false);
        using (await _userLock.LockAsync(userId).ConfigureAwait(false))
        {
            return await CompleteAuthenticationUnderLockAsync(
                userId,
                outcome,
                remoteEndPoint,
                isUserSession,
                startingProviderId,
                providerCreatedUser: false,
                newUserPolicy: null,
                passwordHashUpgrade: null,
                expectedHandoff: handoff).ConfigureAwait(false);
        }
    }

    private async Task<User?> AuthenticateDiscoveredUserAsync(
        string username,
        string password,
        string remoteEndPoint,
        bool isUserSession)
    {
        await _authenticationDiscoveryMutex.WaitAsync().ConfigureAwait(false);
        User? appearedDuringDiscovery = null;
        AuthenticationOutcome? outcome = null;
        UserPolicy? newUserPolicy = null;
        AuthenticationHandoffSnapshot? handoff = null;

        try
        {
            using (var dbContext = _dbProvider.CreateDbContext())
            {
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
                appearedDuringDiscovery = await UserQuery(dbContext)
                    .FirstOrDefaultAsync(user => user.NormalizedUsername == username.ToUpperInvariant())
                    .ConfigureAwait(false);
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
            }

            if (appearedDuringDiscovery is null)
            {
                outcome = await AuthenticateWithProvidersAsync(GetAuthenticationProviders(null), username, password, null).ConfigureAwait(false);
                if (!outcome.Success || outcome.AuthenticationProvider is null)
                {
                    _logger.LogInformation(
                        "Authentication request for {UserName} has been denied (IP: {IP}).",
                        username,
                        remoteEndPoint);
                    throw new AuthenticationException("Invalid username or password entered.");
                }

                if (outcome.AuthenticationProvider is IHasNewUserPolicy hasNewUserPolicy)
                {
                    newUserPolicy = CopyUserPolicy(hasNewUserPolicy.GetNewUserPolicy());
                }

                string normalizedUsername = outcome.Username.ToUpperInvariant();
                using var resolveContext = _dbProvider.CreateDbContext();
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
                List<User> resolvedUsers = await UserQuery(resolveContext)
                    .Where(user => user.NormalizedUsername == normalizedUsername)
                    .Take(2)
                    .ToListAsync()
                    .ConfigureAwait(false);
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
                if (resolvedUsers.Count != 1)
                {
                    throw new AuthenticationException("The authentication provider did not resolve exactly one account.");
                }

                handoff = AuthenticationHandoffSnapshot.Capture(
                    resolveContext,
                    resolvedUsers[0],
                    outcome.AuthenticationProvider,
                    newUserPolicy);
            }
        }
        finally
        {
            _authenticationDiscoveryMutex.Release();
        }

        if (appearedDuringDiscovery is not null)
        {
            return await AuthenticateKnownUserAsync(
                appearedDuringDiscovery.Id,
                username,
                password,
                remoteEndPoint,
                isUserSession).ConfigureAwait(false);
        }

        if (outcome is null || handoff is null)
        {
            throw new AuthenticationException("Invalid username or password entered.");
        }

        using (await _userLock.LockAsync(handoff.UserId).ConfigureAwait(false))
        {
            return await CompleteAuthenticationUnderLockAsync(
                handoff.UserId,
                outcome,
                remoteEndPoint,
                isUserSession,
                startingProviderId: null,
                providerCreatedUser: true,
                newUserPolicy: newUserPolicy,
                passwordHashUpgrade: null,
                expectedHandoff: handoff).ConfigureAwait(false);
        }
    }

    private async Task<AuthenticationOutcome> AuthenticateWithProvidersAsync(
        IReadOnlyList<IAuthenticationProvider> providers,
        string username,
        string password,
        User? resolvedUser)
    {
        string currentUsername = username;
        IAuthenticationProvider? lastProvider = null;
        foreach (IAuthenticationProvider provider in providers)
        {
            lastProvider = provider;
            (string updatedUsername, bool success) = await AuthenticateWithProvider(
                provider,
                currentUsername,
                password,
                resolvedUser).ConfigureAwait(false);
            currentUsername = updatedUsername;
            if (success)
            {
                return new AuthenticationOutcome(provider, currentUsername, true);
            }
        }

        return new AuthenticationOutcome(lastProvider, currentUsername, false);
    }

    private async Task<AuthenticationHandoffSnapshot> CaptureHandoffByIdAsync(
        Guid userId,
        IAuthenticationProvider? provider,
        UserPolicy? newUserPolicy)
    {
        using var dbContext = _dbProvider.CreateDbContext();
        User? user = await UserQuery(dbContext)
            .FirstOrDefaultAsync(value => value.Id.Equals(userId))
            .ConfigureAwait(false);
        return user is null
            ? throw new AuthenticationException("The account no longer exists.")
            : AuthenticationHandoffSnapshot.Capture(dbContext, user, provider, newUserPolicy);
    }

    private async Task<User?> CompleteAuthenticationUnderLockAsync(
        Guid userId,
        AuthenticationOutcome outcome,
        string remoteEndPoint,
        bool isUserSession,
        string? startingProviderId,
        bool providerCreatedUser,
        UserPolicy? newUserPolicy,
        PasswordHashUpgrade? passwordHashUpgrade,
        AuthenticationHandoffSnapshot? expectedHandoff)
    {
        using var dbContext = _dbProvider.CreateDbContext();
        IQueryable<User> userQuery = UserQuery(dbContext).AsTracking();
        if (expectedHandoff is not null)
        {
            userQuery = userQuery.Where(user => user.NormalizedUsername == expectedHandoff.NormalizedUsername);
        }

        User? user = await userQuery
            .FirstOrDefaultAsync(value => value.Id.Equals(userId))
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new AuthenticationException("The account no longer exists or its identity changed during authentication.");
        }

        if (expectedHandoff is not null
            && !expectedHandoff.Matches(dbContext, user, outcome.AuthenticationProvider, newUserPolicy))
        {
            throw new AuthenticationException("The account changed during authentication.");
        }

        AuthenticationHandoffSnapshot currentSnapshot = expectedHandoff
            ?? AuthenticationHandoffSnapshot.Capture(dbContext, user, outcome.AuthenticationProvider, newUserPolicy);
        if (outcome.Success)
        {
            IAuthenticationProvider provider = outcome.AuthenticationProvider
                ?? throw new AuthenticationException("The authentication provider could not be identified.");
            string selectedProviderId = GetRequiredProviderId(provider);
            ValidateProviderAssignment(
                user,
                selectedProviderId,
                startingProviderId,
                providerCreatedUser,
                newUserPolicy);

            if (providerCreatedUser && newUserPolicy is not null)
            {
                ApplyNewUserPolicy(user, newUserPolicy);
                dbContext.Entry(user).Property(value => value.RowVersion).IsModified = true;
                dbContext.Entry(user).Property(value => value.Password).IsModified = false;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);

                currentSnapshot = AuthenticationHandoffSnapshot.Capture(
                    dbContext,
                    user,
                    provider,
                    newUserPolicy);
                dbContext.ChangeTracker.Clear();
                user = await UserQuery(dbContext)
                    .AsTracking()
                    .FirstOrDefaultAsync(value => value.Id.Equals(userId)
                        && value.NormalizedUsername == currentSnapshot.NormalizedUsername)
                    .ConfigureAwait(false);
                if (user is null || !currentSnapshot.Matches(dbContext, user, provider, newUserPolicy))
                {
                    throw new AuthenticationException("The account changed while its new-user policy was being applied.");
                }

                ValidateProviderAssignment(user, selectedProviderId, startingProviderId, providerCreatedUser, newUserPolicy);
            }

            user = await PublishAuthenticationProviderAsync(dbContext, user, selectedProviderId, currentSnapshot).ConfigureAwait(false);
            EnsureAuthenticationAllowed(user, remoteEndPoint);

            if (passwordHashUpgrade is not null)
            {
                user = await PersistPasswordHashUpgradeAsync(dbContext, passwordHashUpgrade).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
            }

            if (isUserSession)
            {
                DateTime loginDate = DateTime.UtcNow;
                int updatedRows = await dbContext.Users
                    .Where(value => value.Id.Equals(userId))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(value => value.LastActivityDate, loginDate)
                        .SetProperty(value => value.LastLoginDate, loginDate)
                        .SetProperty(value => value.RowVersion, value => value.RowVersion + 1))
                    .ConfigureAwait(false);
                RequireOneAuthenticationRow(updatedRows, "The account no longer exists while recording login activity.");
                dbContext.ChangeTracker.Clear();
            }

            int resetRows = await dbContext.Users
                .Where(value => value.Id.Equals(userId))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.InvalidLoginAttemptCount, 0)
                    .SetProperty(value => value.RowVersion, value => value.RowVersion + 1))
                .ConfigureAwait(false);
            RequireOneAuthenticationRow(resetRows, "The account no longer exists while resetting failed login attempts.");
            dbContext.ChangeTracker.Clear();

            user = await UserQuery(dbContext)
                .FirstOrDefaultAsync(value => value.Id.Equals(userId))
                .ConfigureAwait(false)
                ?? throw new AuthenticationException("The account no longer exists after authentication.");
            _logger.LogInformation("Authentication request for {UserName} has succeeded.", user.Username);
            return user;
        }

        EnsureAuthenticationAllowed(user, remoteEndPoint);
        user.InvalidLoginAttemptCount++;
        bool publishLockout = false;
        int? maxInvalidLogins = user.LoginAttemptsBeforeLockout;
        if (maxInvalidLogins.HasValue
            && user.InvalidLoginAttemptCount >= maxInvalidLogins.Value
            && !user.HasPermission(PermissionKind.IsDisabled))
        {
            user.SetPermission(PermissionKind.IsDisabled, true);
            publishLockout = true;
        }

        dbContext.Entry(user).Property(value => value.Password).IsModified = false;
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        if (publishLockout)
        {
            _logger.LogWarning(
                "Disabling user {Username} due to {Attempts} unsuccessful login attempts.",
                user.Username,
                user.InvalidLoginAttemptCount);
            await _eventManager.PublishAsync(new UserLockedOutEventArgs(user)).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Authentication request for {UserName} has been denied (IP: {IP}).",
            user.Username,
            remoteEndPoint);
        return null;
    }

    private async Task<User> PublishAuthenticationProviderAsync(
        JellyfinDbContext dbContext,
        User user,
        string selectedProviderId,
        AuthenticationHandoffSnapshot expectedSnapshot)
    {
        if (string.Equals(user.AuthenticationProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase))
        {
            if (!expectedSnapshot.Matches(dbContext, user, expectedSnapshot.SelectedProvider, expectedSnapshot.NewUserPolicy))
            {
                throw new AuthenticationException("The account changed before its authentication provider could be confirmed.");
            }

            return user;
        }

        Guid userId = user.Id;
        string normalizedUsername = user.NormalizedUsername;
        uint expectedRowVersion = user.RowVersion;
        string? expectedProviderId = user.AuthenticationProviderId;
        int updatedRows = await dbContext.Users
            .Where(value => value.Id.Equals(userId)
                && value.NormalizedUsername == normalizedUsername
                && value.RowVersion == expectedRowVersion
                && value.AuthenticationProviderId == expectedProviderId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.AuthenticationProviderId, selectedProviderId)
                .SetProperty(value => value.RowVersion, value => value.RowVersion + 1))
            .ConfigureAwait(false);
        RequireOneAuthenticationRow(updatedRows, "The account changed before its authentication provider could be recorded.");
        dbContext.ChangeTracker.Clear();
        User updatedUser = await UserQuery(dbContext)
            .AsTracking()
            .FirstOrDefaultAsync(value => value.Id.Equals(userId)
                && value.NormalizedUsername == normalizedUsername)
            .ConfigureAwait(false)
            ?? throw new AuthenticationException("The account no longer exists after provider assignment.");
        if (!expectedSnapshot.MatchesAfterProviderAssignment(
            dbContext,
            updatedUser,
            expectedSnapshot.SelectedProvider,
            expectedSnapshot.NewUserPolicy,
            selectedProviderId))
        {
            throw new AuthenticationException("The account changed while its authentication provider was being recorded.");
        }

        return updatedUser;
    }

    private void ValidateProviderAssignment(
        User user,
        string selectedProviderId,
        string? startingProviderId,
        bool providerCreatedUser,
        UserPolicy? newUserPolicy)
    {
        if (providerCreatedUser)
        {
            string? bootstrapProviderId = _defaultAuthenticationProvider.GetType().FullName;
            if (!IsAllowedNewUserProviderId(user.AuthenticationProviderId, selectedProviderId, bootstrapProviderId)
                || (newUserPolicy?.AuthenticationProviderId is string policyProviderId
                    && !IsAllowedNewUserProviderId(policyProviderId, selectedProviderId, bootstrapProviderId)))
            {
                throw new AuthenticationException("The account has an incompatible authentication provider assignment.");
            }

            return;
        }

        if (string.Equals(user.AuthenticationProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(startingProviderId, string.Empty, StringComparison.Ordinal)
                && string.Equals(user.AuthenticationProviderId, string.Empty, StringComparison.Ordinal)))
        {
            return;
        }

        throw new AuthenticationException("The account authentication provider changed during authentication.");
    }

    private static bool IsAllowedNewUserProviderId(string? providerId, string selectedProviderId, string? bootstrapProviderId)
    {
        return string.Equals(providerId, selectedProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, bootstrapProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerId, string.Empty, StringComparison.Ordinal);
    }

    private static string GetRequiredProviderId(IAuthenticationProvider provider)
    {
        string? providerId = provider.GetType().FullName;
        return string.IsNullOrEmpty(providerId)
            ? throw new AuthenticationException("The authentication provider has no stable identity.")
            : providerId;
    }

    private bool IsRegisteredBuiltInAuthenticationProvider(IAuthenticationProvider provider)
    {
        return ReferenceEquals(provider, _defaultAuthenticationProvider)
            && provider.GetType() == typeof(DefaultAuthenticationProvider);
    }

    private static PasswordHashUpgrade? CreatePasswordHashUpgrade(
        IAuthenticationProvider provider,
        User user,
        string? oldPassword)
    {
        if (provider.GetType() != typeof(DefaultAuthenticationProvider)
            || oldPassword is null
            || user.Password is null
            || string.Equals(oldPassword, user.Password, StringComparison.Ordinal))
        {
            return null;
        }

        return new PasswordHashUpgrade
        {
            UserId = user.Id,
            ExpectedPassword = oldPassword,
            ReplacementPassword = user.Password
        };
    }

    private void EnsureAuthenticationAllowed(User user, string remoteEndPoint)
    {
        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            _logger.LogInformation(
                "Authentication request for {UserName} has been denied because this account is currently disabled (IP: {IP}).",
                user.Username,
                remoteEndPoint);
            throw new SecurityException($"The {user.Username} account is currently disabled. Please consult with your administrator.");
        }

        if (!user.HasPermission(PermissionKind.EnableRemoteAccess)
            && !_networkManager.IsInLocalNetwork(remoteEndPoint))
        {
            _logger.LogInformation(
                "Authentication request for {UserName} forbidden: remote access disabled and user not in local network (IP: {IP}).",
                user.Username,
                remoteEndPoint);
            throw new SecurityException("Forbidden.");
        }

        if (!user.IsParentalScheduleAllowed())
        {
            _logger.LogInformation(
                "Authentication request for {UserName} is not allowed at this time due parental restrictions (IP: {IP}).",
                user.Username,
                remoteEndPoint);
            throw new SecurityException("User is not allowed access at this time.");
        }
    }

    private static void RequireOneAuthenticationRow(int rows, string message)
    {
        if (rows != 1)
        {
            throw new AuthenticationException(message);
        }
    }

    private IDisposable EnterAuthenticationProviderCallback()
    {
        bool previous = _insideAuthenticationProviderCallback.Value;
        _insideAuthenticationProviderCallback.Value = true;
        return new AuthenticationCallbackScope(this, previous);
    }

    private void ExitAuthenticationProviderCallback(bool previous)
    {
        _insideAuthenticationProviderCallback.Value = previous;
    }

    private static void ApplyNewUserPolicy(User user, UserPolicy policy)
    {
        int? maxLoginAttempts = policy.LoginAttemptsBeforeLockout switch
        {
            -1 => null,
            0 => 3,
            _ => policy.LoginAttemptsBeforeLockout
        };

        user.MaxParentalRatingScore = policy.MaxParentalRating;
        user.MaxParentalRatingSubScore = policy.MaxParentalSubRating;
        user.EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess;
        user.RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit;
        if (policy.AuthenticationProviderId is not null)
        {
            user.AuthenticationProviderId = policy.AuthenticationProviderId;
        }

        if (policy.PasswordResetProviderId is not null)
        {
            user.PasswordResetProviderId = policy.PasswordResetProviderId;
        }

        user.InvalidLoginAttemptCount = policy.InvalidLoginAttemptCount;
        user.LoginAttemptsBeforeLockout = maxLoginAttempts;
        user.MaxActiveSessions = policy.MaxActiveSessions;
        user.SyncPlayAccess = policy.SyncPlayAccess;
        user.SetPermission(PermissionKind.IsAdministrator, policy.IsAdministrator);
        user.SetPermission(PermissionKind.IsHidden, policy.IsHidden);
        user.SetPermission(PermissionKind.IsDisabled, policy.IsDisabled);
        user.SetPermission(PermissionKind.EnableSharedDeviceControl, policy.EnableSharedDeviceControl);
        user.SetPermission(PermissionKind.EnableRemoteAccess, policy.EnableRemoteAccess);
        user.SetPermission(PermissionKind.EnableLiveTvManagement, policy.EnableLiveTvManagement);
        user.SetPermission(PermissionKind.EnableLiveTvAccess, policy.EnableLiveTvAccess);
        user.SetPermission(PermissionKind.EnableMediaPlayback, policy.EnableMediaPlayback);
        user.SetPermission(PermissionKind.EnableAudioPlaybackTranscoding, policy.EnableAudioPlaybackTranscoding);
        user.SetPermission(PermissionKind.EnableVideoPlaybackTranscoding, policy.EnableVideoPlaybackTranscoding);
        user.SetPermission(PermissionKind.EnableContentDeletion, policy.EnableContentDeletion);
        user.SetPermission(PermissionKind.EnableContentDownloading, policy.EnableContentDownloading);
        user.SetPermission(PermissionKind.EnableSyncTranscoding, policy.EnableSyncTranscoding);
        user.SetPermission(PermissionKind.EnableMediaConversion, policy.EnableMediaConversion);
        user.SetPermission(PermissionKind.EnableAllChannels, policy.EnableAllChannels);
        user.SetPermission(PermissionKind.EnableAllDevices, policy.EnableAllDevices);
        user.SetPermission(PermissionKind.EnableAllFolders, policy.EnableAllFolders);
        user.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, policy.EnableRemoteControlOfOtherUsers);
        user.SetPermission(PermissionKind.EnablePlaybackRemuxing, policy.EnablePlaybackRemuxing);
        user.SetPermission(PermissionKind.EnableCollectionManagement, policy.EnableCollectionManagement);
        user.SetPermission(PermissionKind.EnableSubtitleManagement, policy.EnableSubtitleManagement);
        user.SetPermission(PermissionKind.EnableLyricManagement, policy.EnableLyricManagement);
        user.SetPermission(PermissionKind.ForceRemoteSourceTranscoding, policy.ForceRemoteSourceTranscoding);
        user.SetPermission(PermissionKind.EnablePublicSharing, policy.EnablePublicSharing);

        user.AccessSchedules.Clear();
        if (policy.AccessSchedules is not null)
        {
            foreach (var schedule in policy.AccessSchedules)
            {
                user.AccessSchedules.Add(new AccessSchedule(schedule.DayOfWeek, schedule.StartHour, schedule.EndHour, user.Id));
            }
        }

        user.SetPreference(PreferenceKind.BlockUnratedItems, policy.BlockUnratedItems ?? Array.Empty<UnratedItem>());
        user.SetPreference(PreferenceKind.BlockedTags, policy.BlockedTags ?? Array.Empty<string>());
        user.SetPreference(PreferenceKind.AllowedTags, policy.AllowedTags ?? Array.Empty<string>());
        user.SetPreference(PreferenceKind.EnabledChannels, policy.EnabledChannels ?? Array.Empty<Guid>());
        user.SetPreference(PreferenceKind.EnabledDevices, policy.EnabledDevices ?? Array.Empty<string>());
        user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders ?? Array.Empty<Guid>());
        user.SetPreference(
            PreferenceKind.EnableContentDeletionFromFolders,
            policy.EnableContentDeletionFromFolders ?? Array.Empty<string>());
    }

    private static UserPolicy CopyUserPolicy(UserPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new UserPolicy
        {
            IsAdministrator = policy.IsAdministrator,
            IsHidden = policy.IsHidden,
            EnableCollectionManagement = policy.EnableCollectionManagement,
            EnableSubtitleManagement = policy.EnableSubtitleManagement,
            EnableLyricManagement = policy.EnableLyricManagement,
            IsDisabled = policy.IsDisabled,
            MaxParentalRating = policy.MaxParentalRating,
            MaxParentalSubRating = policy.MaxParentalSubRating,
            BlockedTags = policy.BlockedTags?.ToArray()!,
            AllowedTags = policy.AllowedTags?.ToArray()!,
            EnableUserPreferenceAccess = policy.EnableUserPreferenceAccess,
            AccessSchedules = policy.AccessSchedules?.Select(value => new AccessSchedule(
                value.DayOfWeek,
                value.StartHour,
                value.EndHour,
                value.UserId)).ToArray()!,
            BlockUnratedItems = policy.BlockUnratedItems?.ToArray()!,
            EnableRemoteControlOfOtherUsers = policy.EnableRemoteControlOfOtherUsers,
            EnableSharedDeviceControl = policy.EnableSharedDeviceControl,
            EnableRemoteAccess = policy.EnableRemoteAccess,
            EnableLiveTvManagement = policy.EnableLiveTvManagement,
            EnableLiveTvAccess = policy.EnableLiveTvAccess,
            EnableMediaPlayback = policy.EnableMediaPlayback,
            EnableAudioPlaybackTranscoding = policy.EnableAudioPlaybackTranscoding,
            EnableVideoPlaybackTranscoding = policy.EnableVideoPlaybackTranscoding,
            EnablePlaybackRemuxing = policy.EnablePlaybackRemuxing,
            ForceRemoteSourceTranscoding = policy.ForceRemoteSourceTranscoding,
            EnableContentDeletion = policy.EnableContentDeletion,
            EnableContentDeletionFromFolders = policy.EnableContentDeletionFromFolders?.ToArray()!,
            EnableContentDownloading = policy.EnableContentDownloading,
            EnableSyncTranscoding = policy.EnableSyncTranscoding,
            EnableMediaConversion = policy.EnableMediaConversion,
            EnabledDevices = policy.EnabledDevices?.ToArray()!,
            EnableAllDevices = policy.EnableAllDevices,
            EnabledChannels = policy.EnabledChannels?.ToArray()!,
            EnableAllChannels = policy.EnableAllChannels,
            EnabledFolders = policy.EnabledFolders?.ToArray()!,
            EnableAllFolders = policy.EnableAllFolders,
            InvalidLoginAttemptCount = policy.InvalidLoginAttemptCount,
            LoginAttemptsBeforeLockout = policy.LoginAttemptsBeforeLockout,
            MaxActiveSessions = policy.MaxActiveSessions,
            EnablePublicSharing = policy.EnablePublicSharing,
            BlockedMediaFolders = policy.BlockedMediaFolders?.ToArray()!,
            BlockedChannels = policy.BlockedChannels?.ToArray()!,
            RemoteClientBitrateLimit = policy.RemoteClientBitrateLimit,
            AuthenticationProviderId = policy.AuthenticationProviderId,
            PasswordResetProviderId = policy.PasswordResetProviderId,
            SyncPlayAccess = policy.SyncPlayAccess
        };
    }

    private readonly record struct PermissionValue(int Id, Guid? UserId, PermissionKind Kind, bool Value, uint RowVersion);

    private readonly record struct PreferenceValue(int Id, Guid? UserId, PreferenceKind Kind, string Value, uint RowVersion);

    private readonly record struct AccessScheduleValue(
        int Id,
        Guid UserId,
        DynamicDayOfWeek DayOfWeek,
        long StartHourBits,
        long EndHourBits);

    private readonly record struct ProfileImageValue(int Id, Guid? UserId, string Path, long LastModifiedBinary);

    private sealed class AuthenticationOutcome
    {
        public AuthenticationOutcome(IAuthenticationProvider? authenticationProvider, string username, bool success)
        {
            AuthenticationProvider = authenticationProvider;
            Username = username;
            Success = success;
        }

        public IAuthenticationProvider? AuthenticationProvider { get; }

        public string Username { get; }

        public bool Success { get; }
    }

    private sealed class AuthenticationCallbackScope : IDisposable
    {
        private readonly UserManager _owner;
        private readonly bool _previous;
        private int _disposed;

        public AuthenticationCallbackScope(UserManager owner, bool previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.ExitAuthenticationProviderCallback(_previous);
            }
        }
    }

    private sealed class AuthenticationHandoffSnapshot
    {
        private readonly ScalarValue[] _scalars;
        private readonly PermissionValue[] _permissions;
        private readonly PreferenceValue[] _preferences;
        private readonly AccessScheduleValue[] _accessSchedules;
        private readonly ProfileImageValue? _profileImage;
        private readonly IAuthenticationProvider? _selectedProvider;
        private readonly Type? _selectedProviderType;
        private readonly UserPolicy? _newUserPolicy;

        private AuthenticationHandoffSnapshot(
            Guid userId,
            string normalizedUsername,
            ScalarValue[] scalars,
            PermissionValue[] permissions,
            PreferenceValue[] preferences,
            AccessScheduleValue[] accessSchedules,
            ProfileImageValue? profileImage,
            IAuthenticationProvider? selectedProvider,
            UserPolicy? newUserPolicy)
        {
            UserId = userId;
            NormalizedUsername = normalizedUsername;
            _scalars = scalars;
            _permissions = permissions;
            _preferences = preferences;
            _accessSchedules = accessSchedules;
            _profileImage = profileImage;
            _selectedProvider = selectedProvider;
            _selectedProviderType = selectedProvider?.GetType();
            _newUserPolicy = newUserPolicy;
        }

        public Guid UserId { get; }

        public string NormalizedUsername { get; }

        public IAuthenticationProvider? SelectedProvider => _selectedProvider;

        public UserPolicy? NewUserPolicy => _newUserPolicy;

        public static AuthenticationHandoffSnapshot Capture(
            JellyfinDbContext dbContext,
            User user,
            IAuthenticationProvider? selectedProvider,
            UserPolicy? newUserPolicy)
        {
            var scalarValues = dbContext.Entry(user).Properties
                .OrderBy(property => property.Metadata.Name, StringComparer.Ordinal)
                .Select(property => new ScalarValue(property.Metadata.Name, CloneValue(property.CurrentValue)))
                .ToArray();
            PermissionValue[] permissions = user.Permissions
                .Select(value => new PermissionValue(value.Id, value.UserId, value.Kind, value.Value, value.RowVersion))
                .OrderBy(value => value.Id)
                .ThenBy(value => value.UserId)
                .ThenBy(value => value.Kind)
                .ThenBy(value => value.Value)
                .ThenBy(value => value.RowVersion)
                .ToArray();
            PreferenceValue[] preferences = user.Preferences
                .Select(value => new PreferenceValue(value.Id, value.UserId, value.Kind, value.Value, value.RowVersion))
                .OrderBy(value => value.Id)
                .ThenBy(value => value.UserId)
                .ThenBy(value => value.Kind)
                .ThenBy(value => value.Value, StringComparer.Ordinal)
                .ThenBy(value => value.RowVersion)
                .ToArray();
            AccessScheduleValue[] accessSchedules = user.AccessSchedules
                .Select(value => new AccessScheduleValue(
                    value.Id,
                    value.UserId,
                    value.DayOfWeek,
                    BitConverter.DoubleToInt64Bits(value.StartHour),
                    BitConverter.DoubleToInt64Bits(value.EndHour)))
                .OrderBy(value => value.Id)
                .ThenBy(value => value.UserId)
                .ThenBy(value => value.DayOfWeek)
                .ThenBy(value => value.StartHourBits)
                .ThenBy(value => value.EndHourBits)
                .ToArray();
            ProfileImageValue? profileImage = user.ProfileImage is null
                ? null
                : new ProfileImageValue(
                    user.ProfileImage.Id,
                    user.ProfileImage.UserId,
                    user.ProfileImage.Path,
                    user.ProfileImage.LastModified.ToBinary());

            return new AuthenticationHandoffSnapshot(
                user.Id,
                user.NormalizedUsername,
                scalarValues,
                permissions,
                preferences,
                accessSchedules,
                profileImage,
                selectedProvider,
                newUserPolicy);
        }

        public bool Matches(
            JellyfinDbContext dbContext,
            User user,
            IAuthenticationProvider? selectedProvider,
            UserPolicy? newUserPolicy)
        {
            if (!UserId.Equals(user.Id)
                || !string.Equals(NormalizedUsername, user.NormalizedUsername, StringComparison.Ordinal)
                || !ReferenceEquals(_selectedProvider, selectedProvider)
                || _selectedProviderType != selectedProvider?.GetType()
                || !ReferenceEquals(_newUserPolicy, newUserPolicy))
            {
                return false;
            }

            AuthenticationHandoffSnapshot current = Capture(dbContext, user, selectedProvider, newUserPolicy);
            return ScalarSequencesEqual(_scalars, current._scalars)
                && _permissions.SequenceEqual(current._permissions)
                && _preferences.SequenceEqual(current._preferences)
                && _accessSchedules.SequenceEqual(current._accessSchedules)
                && Nullable.Equals(_profileImage, current._profileImage);
        }

        public bool MatchesAfterProviderAssignment(
            JellyfinDbContext dbContext,
            User user,
            IAuthenticationProvider? selectedProvider,
            UserPolicy? newUserPolicy,
            string assignedProviderId)
        {
            if (!UserId.Equals(user.Id)
                || !string.Equals(NormalizedUsername, user.NormalizedUsername, StringComparison.Ordinal)
                || !ReferenceEquals(_selectedProvider, selectedProvider)
                || _selectedProviderType != selectedProvider?.GetType()
                || !ReferenceEquals(_newUserPolicy, newUserPolicy)
                || !string.Equals(user.AuthenticationProviderId, assignedProviderId, StringComparison.Ordinal)
                || !HasExpectedNextRowVersion(user.RowVersion))
            {
                return false;
            }

            AuthenticationHandoffSnapshot current = Capture(dbContext, user, selectedProvider, newUserPolicy);
            return ScalarSequencesEqualExceptProviderAndVersion(_scalars, current._scalars)
                && _permissions.SequenceEqual(current._permissions)
                && _preferences.SequenceEqual(current._preferences)
                && _accessSchedules.SequenceEqual(current._accessSchedules)
                && Nullable.Equals(_profileImage, current._profileImage);
        }

        private bool HasExpectedNextRowVersion(uint currentVersion)
        {
            ScalarValue? rowVersion = _scalars.FirstOrDefault(value => string.Equals(value.Name, nameof(User.RowVersion), StringComparison.Ordinal));
            return rowVersion?.Value is uint version
                && version != uint.MaxValue
                && currentVersion == version + 1;
        }

        private static bool ScalarSequencesEqual(ScalarValue[] left, ScalarValue[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int index = 0; index < left.Length; index++)
            {
                if (!string.Equals(left[index].Name, right[index].Name, StringComparison.Ordinal)
                    || !ValuesEqual(left[index].Value, right[index].Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ScalarSequencesEqualExceptProviderAndVersion(ScalarValue[] left, ScalarValue[] right)
        {
            ScalarValue[] leftCompared = left
                .Where(value => !string.Equals(value.Name, nameof(User.AuthenticationProviderId), StringComparison.Ordinal)
                    && !string.Equals(value.Name, nameof(User.RowVersion), StringComparison.Ordinal))
                .ToArray();
            ScalarValue[] rightCompared = right
                .Where(value => !string.Equals(value.Name, nameof(User.AuthenticationProviderId), StringComparison.Ordinal)
                    && !string.Equals(value.Name, nameof(User.RowVersion), StringComparison.Ordinal))
                .ToArray();
            return ScalarSequencesEqual(leftCompared, rightCompared);
        }

        private static object? CloneValue(object? value)
        {
            return value switch
            {
                byte[] bytes => bytes.ToArray(),
                char[] chars => chars.ToArray(),
                _ => value
            };
        }

        private static bool ValuesEqual(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.GetType() != right.GetType())
            {
                return false;
            }

            return (left, right) switch
            {
                (byte[] leftBytes, byte[] rightBytes) => leftBytes.SequenceEqual(rightBytes),
                (char[] leftChars, char[] rightChars) => leftChars.SequenceEqual(rightChars),
                (DateTime leftDate, DateTime rightDate) => leftDate.ToBinary() == rightDate.ToBinary(),
                (DateTimeOffset leftDate, DateTimeOffset rightDate) => leftDate.EqualsExact(rightDate),
                _ => left.Equals(right)
            };
        }

        private sealed class ScalarValue
        {
            public ScalarValue(string name, object? value)
            {
                Name = name;
                Value = value;
            }

            public string Name { get; }

            public object? Value { get; }
        }
    }
}
