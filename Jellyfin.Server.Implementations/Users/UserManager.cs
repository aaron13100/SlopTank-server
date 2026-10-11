// SlopTank modification notice: added or changed by SlopTank on 2026-10-12.
#pragma warning disable RS0030 // Do not use banned APIs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Manages the creation and retrieval of <see cref="User"/> instances.
    /// </summary>
    public partial class UserManager : IUserManager, IDisposable
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
        private readonly IEventManager _eventManager;
        private readonly INetworkManager _networkManager;
        private readonly IApplicationHost _appHost;
        private readonly IImageProcessor _imageProcessor;
        private readonly ILogger<UserManager> _logger;
        private readonly IReadOnlyCollection<IPasswordResetProvider> _passwordResetProviders;
        private readonly IReadOnlyCollection<IAuthenticationProvider> _authenticationProviders;
        private readonly InvalidAuthProvider _invalidAuthProvider;
        private readonly DefaultAuthenticationProvider _defaultAuthenticationProvider;
        private readonly DefaultPasswordResetProvider _defaultPasswordResetProvider;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        private readonly LockHelper _userLock;
        private readonly SemaphoreSlim _authenticationDiscoveryMutex = new(1, 1);
        private static readonly AsyncLocal<bool> _insideAuthenticationProviderCallback = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="UserManager"/> class.
        /// </summary>
        /// <param name="dbProvider">The database provider.</param>
        /// <param name="eventManager">The event manager.</param>
        /// <param name="networkManager">The network manager.</param>
        /// <param name="appHost">The application host.</param>
        /// <param name="imageProcessor">The image processor.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="serverConfigurationManager">The system config manager.</param>
        /// <param name="passwordResetProviders">The password reset providers.</param>
        /// <param name="authenticationProviders">The authentication providers.</param>
        public UserManager(
            IDbContextFactory<JellyfinDbContext> dbProvider,
            IEventManager eventManager,
            INetworkManager networkManager,
            IApplicationHost appHost,
            IImageProcessor imageProcessor,
            ILogger<UserManager> logger,
            IServerConfigurationManager serverConfigurationManager,
            IEnumerable<IPasswordResetProvider> passwordResetProviders,
            IEnumerable<IAuthenticationProvider> authenticationProviders)
            : this(
                dbProvider,
                eventManager,
                networkManager,
                appHost,
                imageProcessor,
                logger,
                serverConfigurationManager,
                passwordResetProviders,
                authenticationProviders,
                null)
        {
        }

        internal UserManager(
            IDbContextFactory<JellyfinDbContext> dbProvider,
            IEventManager eventManager,
            INetworkManager networkManager,
            IApplicationHost appHost,
            IImageProcessor imageProcessor,
            ILogger<UserManager> logger,
            IServerConfigurationManager serverConfigurationManager,
            IEnumerable<IPasswordResetProvider> passwordResetProviders,
            IEnumerable<IAuthenticationProvider> authenticationProviders,
            IUserLockWaitObserver? userLockWaitObserver)
        {
            _dbProvider = dbProvider;
            _eventManager = eventManager;
            _networkManager = networkManager;
            _appHost = appHost;
            _imageProcessor = imageProcessor;
            _logger = logger;
            _serverConfigurationManager = serverConfigurationManager;

            _passwordResetProviders = passwordResetProviders.ToList();
            _authenticationProviders = authenticationProviders.ToList();

            _userLock = new LockHelper(userLockWaitObserver);

            _invalidAuthProvider = _authenticationProviders.OfType<InvalidAuthProvider>().First();
            _defaultAuthenticationProvider = _authenticationProviders.OfType<DefaultAuthenticationProvider>().First();
            _defaultPasswordResetProvider = _passwordResetProviders.OfType<DefaultPasswordResetProvider>().First();
        }

        /// <inheritdoc/>
        public event EventHandler<GenericEventArgs<User>>? OnUserUpdated;

        /// <inheritdoc/>
        public IEnumerable<User> GetUsers()
        {
            using var dbContext = _dbProvider.CreateDbContext();
            return UserQuery(dbContext)
                .ToArray();
        }

        /// <inheritdoc/>
        public IEnumerable<Guid> GetUsersIds()
        {
            using var dbContext = _dbProvider.CreateDbContext();
            return dbContext.Users
                .AsNoTracking()
                .Select(user => user.Id)
                .ToArray();
        }

        // This is some regex that matches only on unicode "word" characters, as well as -, _ and @
        // In theory this will cut out most if not all 'control' characters which should help minimize any weirdness
        // Usernames can contain letters (a-z + whatever else unicode is cool with), numbers (0-9), at-signs (@), dashes (-), underscores (_), apostrophes ('), periods (.) and spaces ( )
        [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
        private static partial Regex ValidUsernameRegex();

        /// <inheritdoc/>
        public User? GetUserById(Guid id)
        {
            if (id.IsEmpty())
            {
                throw new ArgumentException("Guid can't be empty", nameof(id));
            }

            using var dbContext = _dbProvider.CreateDbContext();
            return UserQuery(dbContext)
                .FirstOrDefault(user => user.Id == id);
        }

        private static IQueryable<User> UserQuery(JellyfinDbContext dbContext)
        {
            return dbContext.Users
                            .AsSingleQuery()
                            .Include(user => user.Permissions)
                            .Include(user => user.Preferences)
                            .Include(user => user.AccessSchedules)
                            .Include(user => user.ProfileImage)
                            .AsNoTracking();
        }

        /// <inheritdoc/>
        public User? GetFirstUser()
        {
            using var dbContext = _dbProvider.CreateDbContext();
            return UserQuery(dbContext).FirstOrDefault();
        }

        /// <inheritdoc/>
        public User? GetUserByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Invalid username", nameof(name));
            }

            using var dbContext = _dbProvider.CreateDbContext();
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
            return UserQuery(dbContext)
                .FirstOrDefault(u => u.NormalizedUsername == name.ToUpperInvariant());
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
        }

        /// <inheritdoc/>
        public async Task RenameUser(Guid userId, string oldName, string newName)
        {
            ThrowIfInvalidUsername(newName);

            if (oldName.Equals(newName, StringComparison.Ordinal))
            {
                throw new ArgumentException("The new and old names must be different.");
            }

            User user = null!; // user is never actually null where its used afterwards so we can just ignore.
            using (await _userLock.LockAsync(userId).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
                    if (await dbContext.Users
                            .AnyAsync(u => u.NormalizedUsername == newName.ToUpperInvariant() && u.Id != userId)
                            .ConfigureAwait(false))
                    {
                        throw new ArgumentException(string.Format(
                            CultureInfo.InvariantCulture,
                            "A user with the name '{0}' already exists.",
                            newName));
                    }
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

                    user = await UserQuery(dbContext)
                        .AsTracking()
                        .FirstOrDefaultAsync(u => u.Id == userId)
                        .ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException(nameof(userId));
                    user.Username = newName;
                    user.NormalizedUsername = newName.ToUpperInvariant();
                    await UpdateUserInternalAsync(dbContext, user).ConfigureAwait(false);
                }
            }

            var eventArgs = new UserUpdatedEventArgs(user);
            await _eventManager.PublishAsync(eventArgs).ConfigureAwait(false);
            OnUserUpdated?.Invoke(this, eventArgs);
        }

        /// <inheritdoc/>
        public async Task UpdateUserAsync(User user)
        {
            using (await _userLock.LockAsync(user.Id).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    // TODO: this is a bit of a hack. Because the user entity can be created in another context, it is maybe tracked elsewhere and navigation properties do not easily move between context. Solution is to use proper DTOs instead.
                    var dbUser = await UserQuery(dbContext)
                        .AsTracking()
                        .FirstOrDefaultAsync(u => u.Id == user.Id)
                        .ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException(nameof(user.Id));

                    if (user.RowVersion != dbUser.RowVersion)
                    {
                        throw new DbUpdateConcurrencyException(
                            $"The user '{user.Id}' changed from row version {user.RowVersion} to {dbUser.RowVersion} before the update.");
                    }

                    if (!string.Equals(user.Password, dbUser.Password, StringComparison.Ordinal))
                    {
                        throw new CredentialMutationException(
                            $"The user '{user.Id}' password can only be changed through ChangePassword.");
                    }

                    string? persistedPassword = dbUser.Password;
                    uint persistedRowVersion = dbUser.RowVersion;

                    dbContext.Entry(dbUser).CurrentValues.SetValues(user);
                    dbContext.Entry(dbUser).CurrentValues[nameof(User.Password)] = persistedPassword;
                    dbContext.Entry(dbUser).CurrentValues[nameof(User.RowVersion)] = persistedRowVersion;
                    dbContext.Entry(dbUser).Property(value => value.Password).IsModified = false;
                    dbContext.Entry(dbUser).Property(value => value.RowVersion).IsModified = true;
                    dbUser.Permissions.Clear();
                    foreach (var permission in user.Permissions)
                    {
                        dbUser.Permissions.Add(new Permission(permission.Kind, permission.Value));
                    }

                    dbUser.Preferences.Clear();
                    foreach (var preference in user.Preferences)
                    {
                        dbUser.Preferences.Add(new Preference(preference.Kind, preference.Value));
                    }

                    dbUser.AccessSchedules.Clear();
                    foreach (var accessSchedule in user.AccessSchedules)
                    {
                        dbUser.AccessSchedules.Add(new AccessSchedule(accessSchedule.DayOfWeek, accessSchedule.StartHour, accessSchedule.EndHour, dbUser.Id));
                    }

                    if (user.ProfileImage is null)
                    {
                        if (dbUser.ProfileImage is not null)
                        {
                            dbContext.Remove(dbUser.ProfileImage);
                            dbUser.ProfileImage = null;
                        }
                    }
                    else if (dbUser.ProfileImage is null)
                    {
                        dbUser.ProfileImage = new Jellyfin.Database.Implementations.Entities.ImageInfo(user.ProfileImage.Path)
                        {
                            LastModified = user.ProfileImage.LastModified
                        };
                    }
                    else
                    {
                        dbUser.ProfileImage.Path = user.ProfileImage.Path;
                        dbUser.ProfileImage.LastModified = user.ProfileImage.LastModified;
                    }

                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
        }

        internal async Task<User> CreateUserInternalAsync(string name, JellyfinDbContext dbContext)
        {
            // TODO: Remove after user item data is migrated.
            var max = await dbContext.Users.AsQueryable().AnyAsync().ConfigureAwait(false)
                ? await dbContext.Users.AsQueryable().Select(u => u.InternalId).MaxAsync().ConfigureAwait(false)
                : 0;

            var user = new User(
                name,
                _defaultAuthenticationProvider.GetType().FullName!,
                _defaultPasswordResetProvider.GetType().FullName!)
            {
                InternalId = max + 1
            };

            user.AddDefaultPermissions();
            user.AddDefaultPreferences();

            return user;
        }

        /// <inheritdoc/>
        public async Task<User> CreateUserAsync(string name)
        {
            ThrowIfInvalidUsername(name);

            User newUser;
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
#pragma warning disable CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons
                if (await dbContext.Users
                        .AnyAsync(u => u.NormalizedUsername == name.ToUpperInvariant())
                        .ConfigureAwait(false))
                {
                    throw new ArgumentException(string.Format(
                        CultureInfo.InvariantCulture,
                        "A user with the name '{0}' already exists.",
                        name));
                }
#pragma warning restore CA1862 // Use the 'StringComparison' method overloads to perform case-insensitive string comparisons

                newUser = await CreateUserInternalAsync(name, dbContext).ConfigureAwait(false);

                dbContext.Users.Add(newUser);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }

            await _eventManager.PublishAsync(new UserCreatedEventArgs(newUser)).ConfigureAwait(false);

            return newUser;
        }

        /// <inheritdoc/>
        public async Task DeleteUserAsync(Guid userId)
        {
            User? user;
            using (await _userLock.LockAsync(userId).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    user = await dbContext.Users
                        .Include(u => u.Permissions)
                        .FirstOrDefaultAsync(u => u.Id.Equals(userId))
                        .ConfigureAwait(false);
                    if (user is null)
                    {
                        throw new ResourceNotFoundException(nameof(userId));
                    }

                    var userCount = await dbContext.Users.CountAsync().ConfigureAwait(false);
                    if (userCount == 1)
                    {
                        throw new InvalidOperationException(string.Format(
                            CultureInfo.InvariantCulture,
                            "The user '{0}' cannot be deleted because there must be at least one user in the system.",
                            user.Username));
                    }

                    if (user.HasPermission(PermissionKind.IsAdministrator)
                        && await dbContext.Users
                            .CountAsync(i => i.Permissions.Any(p => p.Kind == PermissionKind.IsAdministrator && p.Value))
                            .ConfigureAwait(false) == 1)
                    {
                        throw new ArgumentException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "The user '{0}' cannot be deleted because there must be at least one admin user in the system.",
                                user.Username),
                            nameof(userId));
                    }

                    dbContext.Users.Remove(user);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }

            await _eventManager.PublishAsync(new UserDeletedEventArgs(user)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public Task ResetPassword(Guid userId)
        {
            return ChangePassword(userId, string.Empty);
        }

        /// <inheritdoc/>
        public async Task ChangePassword(Guid userId, string newPassword)
        {
            User dbUser = null!;
            using (await _userLock.LockAsync(userId).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    dbUser = await UserQuery(dbContext)
                        .AsTracking()
                        .FirstOrDefaultAsync(u => u.Id == userId)
                        .ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException(nameof(userId));
                    if (dbUser.HasPermission(PermissionKind.IsAdministrator) && string.IsNullOrWhiteSpace(newPassword))
                    {
                        throw new ArgumentException("Admin user passwords must not be empty", nameof(newPassword));
                    }

                    await GetAuthenticationProvider(dbUser).ChangePassword(dbUser, newPassword).ConfigureAwait(false);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }

            await _eventManager.PublishAsync(new UserPasswordChangedEventArgs(dbUser)).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public UserDto GetUserDto(User user, string? remoteEndPoint = null)
        {
            var castReceiverApplications = _serverConfigurationManager.Configuration.CastReceiverApplications;
            return new UserDto
            {
                Name = user.Username,
                Id = user.Id,
                ServerId = _appHost.SystemId,
                EnableAutoLogin = user.EnableAutoLogin,
                LastLoginDate = user.LastLoginDate,
                LastActivityDate = user.LastActivityDate,
                PrimaryImageTag = user.ProfileImage is not null ? _imageProcessor.GetImageCacheTag(user) : null,
                Configuration = new UserConfiguration
                {
                    SubtitleMode = user.SubtitleMode,
                    HidePlayedInLatest = user.HidePlayedInLatest,
                    EnableLocalPassword = user.EnableLocalPassword,
                    PlayDefaultAudioTrack = user.PlayDefaultAudioTrack,
                    DisplayCollectionsView = user.DisplayCollectionsView,
                    DisplayMissingEpisodes = user.DisplayMissingEpisodes,
                    AudioLanguagePreference = user.AudioLanguagePreference,
                    RememberAudioSelections = user.RememberAudioSelections,
                    EnableNextEpisodeAutoPlay = user.EnableNextEpisodeAutoPlay,
                    RememberSubtitleSelections = user.RememberSubtitleSelections,
                    SubtitleLanguagePreference = user.SubtitleLanguagePreference ?? string.Empty,
                    OrderedViews = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews),
                    GroupedFolders = user.GetPreferenceValues<Guid>(PreferenceKind.GroupedFolders),
                    MyMediaExcludes = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes),
                    LatestItemsExcludes = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes),
                    CastReceiverId = string.IsNullOrEmpty(user.CastReceiverId)
                        ? castReceiverApplications.FirstOrDefault()?.Id
                        : castReceiverApplications.FirstOrDefault(c => string.Equals(c.Id, user.CastReceiverId, StringComparison.Ordinal))?.Id
                          ?? castReceiverApplications.FirstOrDefault()?.Id
                },
                Policy = new UserPolicy
                {
                    MaxParentalRating = user.MaxParentalRatingScore,
                    MaxParentalSubRating = user.MaxParentalRatingSubScore,
                    EnableUserPreferenceAccess = user.EnableUserPreferenceAccess,
                    RemoteClientBitrateLimit = user.RemoteClientBitrateLimit ?? 0,
                    AuthenticationProviderId = user.AuthenticationProviderId,
                    PasswordResetProviderId = user.PasswordResetProviderId,
                    InvalidLoginAttemptCount = user.InvalidLoginAttemptCount,
                    LoginAttemptsBeforeLockout = user.LoginAttemptsBeforeLockout ?? -1,
                    MaxActiveSessions = user.MaxActiveSessions,
                    IsAdministrator = user.HasPermission(PermissionKind.IsAdministrator),
                    IsHidden = user.HasPermission(PermissionKind.IsHidden),
                    IsDisabled = user.HasPermission(PermissionKind.IsDisabled),
                    EnableSharedDeviceControl = user.HasPermission(PermissionKind.EnableSharedDeviceControl),
                    EnableRemoteAccess = user.HasPermission(PermissionKind.EnableRemoteAccess),
                    EnableLiveTvManagement = user.HasPermission(PermissionKind.EnableLiveTvManagement),
                    EnableLiveTvAccess = user.HasPermission(PermissionKind.EnableLiveTvAccess),
                    EnableMediaPlayback = user.HasPermission(PermissionKind.EnableMediaPlayback),
                    EnableAudioPlaybackTranscoding = user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding),
                    EnableVideoPlaybackTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
                    EnableContentDeletion = user.HasPermission(PermissionKind.EnableContentDeletion),
                    EnableContentDownloading = user.HasPermission(PermissionKind.EnableContentDownloading),
                    EnableSyncTranscoding = user.HasPermission(PermissionKind.EnableSyncTranscoding),
                    EnableMediaConversion = user.HasPermission(PermissionKind.EnableMediaConversion),
                    EnableAllChannels = user.HasPermission(PermissionKind.EnableAllChannels),
                    EnableAllDevices = user.HasPermission(PermissionKind.EnableAllDevices),
                    EnableAllFolders = user.HasPermission(PermissionKind.EnableAllFolders),
                    EnableRemoteControlOfOtherUsers = user.HasPermission(PermissionKind.EnableRemoteControlOfOtherUsers),
                    EnablePlaybackRemuxing = user.HasPermission(PermissionKind.EnablePlaybackRemuxing),
                    ForceRemoteSourceTranscoding = user.HasPermission(PermissionKind.ForceRemoteSourceTranscoding),
                    EnablePublicSharing = user.HasPermission(PermissionKind.EnablePublicSharing),
                    EnableCollectionManagement = user.HasPermission(PermissionKind.EnableCollectionManagement),
                    EnableSubtitleManagement = user.HasPermission(PermissionKind.EnableSubtitleManagement),
                    AccessSchedules = user.AccessSchedules.ToArray(),
                    BlockedTags = user.GetPreference(PreferenceKind.BlockedTags),
                    AllowedTags = user.GetPreference(PreferenceKind.AllowedTags),
                    EnabledChannels = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledChannels),
                    EnabledDevices = user.GetPreference(PreferenceKind.EnabledDevices),
                    EnabledFolders = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
                    EnableContentDeletionFromFolders = user.GetPreference(PreferenceKind.EnableContentDeletionFromFolders),
                    SyncPlayAccess = user.SyncPlayAccess,
                    BlockedChannels = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedChannels),
                    BlockedMediaFolders = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders),
                    BlockUnratedItems = user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems)
                }
            };
        }

        /// <inheritdoc/>
        public async Task<User?> AuthenticateUser(
            string username,
            string password,
            string remoteEndPoint,
            bool isUserSession)
        {
            if (_insideAuthenticationProviderCallback.Value)
            {
                throw new AuthenticationException("Authentication cannot be invoked recursively by an authentication provider.");
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                _logger.LogInformation("Authentication request without username has been denied (IP: {IP}).", remoteEndPoint);
                throw new ArgumentNullException(nameof(username));
            }

            var initialUser = GetUserByName(username);
            if (initialUser is not null)
            {
                return await AuthenticateKnownUserAsync(initialUser.Id, username, password, remoteEndPoint, isUserSession).ConfigureAwait(false);
            }

            return await AuthenticateDiscoveredUserAsync(username, password, remoteEndPoint, isUserSession).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async Task<ForgotPasswordResult> StartForgotPasswordProcess(string enteredUsername, bool isInNetwork)
        {
            var user = string.IsNullOrWhiteSpace(enteredUsername) ? null : GetUserByName(enteredUsername);
            var passwordResetProvider = GetPasswordResetProvider(user);

            var result = await passwordResetProvider
                .StartForgotPasswordProcess(user, enteredUsername, isInNetwork)
                .ConfigureAwait(false);

            bool isBuiltInPasswordResetProvider = ReferenceEquals(passwordResetProvider, _defaultPasswordResetProvider)
                && passwordResetProvider.GetType() == typeof(DefaultPasswordResetProvider);
            if (user is not null && isInNetwork && !isBuiltInPasswordResetProvider)
            {
                try
                {
                    await UpdateUserAsync(user).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DbUpdateConcurrencyException || ex is CredentialMutationException)
                {
                    _logger.LogWarning(
                        ex,
                        "Password reset provider {Provider} completed, but its secondary user update was refused for user {UserId}.",
                        passwordResetProvider.GetType().FullName,
                        user.Id);
                }
            }

            return result;
        }

        /// <inheritdoc/>
        public async Task<PinRedeemResult> RedeemPasswordResetPin(string pin)
        {
            foreach (var provider in _passwordResetProviders)
            {
                var result = await provider.RedeemPasswordResetPin(pin).ConfigureAwait(false);

                if (result.Success)
                {
                    return result;
                }
            }

            return new PinRedeemResult();
        }

        /// <inheritdoc />
        public async Task InitializeAsync()
        {
            // TODO: Refactor the startup wizard so that it doesn't require a user to already exist.
            var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                if (await dbContext.Users.AnyAsync().ConfigureAwait(false))
                {
                    return;
                }

                var defaultName = Environment.UserName;
                if (string.IsNullOrWhiteSpace(defaultName) || !ValidUsernameRegex().IsMatch(defaultName))
                {
                    defaultName = "MyJellyfinUser";
                }

                _logger.LogWarning("No users, creating one with username {UserName}", defaultName);

                var newUser = await CreateUserInternalAsync(defaultName, dbContext).ConfigureAwait(false);
                newUser.SetPermission(PermissionKind.IsAdministrator, true);
                newUser.SetPermission(PermissionKind.EnableContentDeletion, true);
                newUser.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

                dbContext.Users.Add(newUser);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        /// <inheritdoc/>
        public NameIdPair[] GetAuthenticationProviders()
        {
            return _authenticationProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultAuthenticationProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public NameIdPair[] GetPasswordResetProviders()
        {
            return _passwordResetProviders
                .Where(provider => provider.IsEnabled)
                .OrderBy(i => i is DefaultPasswordResetProvider ? 0 : 1)
                .ThenBy(i => i.Name)
                .Select(i => new NameIdPair
                {
                    Name = i.Name,
                    Id = i.GetType().FullName
                })
                .ToArray();
        }

        /// <inheritdoc/>
        public async Task UpdateConfigurationAsync(Guid userId, UserConfiguration config)
        {
            using (await _userLock.LockAsync(userId).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    var user = UserQuery(dbContext)
                                   .AsTracking()
                                   .FirstOrDefault(u => u.Id.Equals(userId))
                               ?? throw new ArgumentException("No user exists with given Id!");

                    user.SubtitleMode = config.SubtitleMode;
                    user.HidePlayedInLatest = config.HidePlayedInLatest;
                    user.EnableLocalPassword = config.EnableLocalPassword;
                    user.PlayDefaultAudioTrack = config.PlayDefaultAudioTrack;
                    user.DisplayCollectionsView = config.DisplayCollectionsView;
                    user.DisplayMissingEpisodes = config.DisplayMissingEpisodes;
                    user.AudioLanguagePreference = config.AudioLanguagePreference;
                    user.RememberAudioSelections = config.RememberAudioSelections;
                    user.EnableNextEpisodeAutoPlay = config.EnableNextEpisodeAutoPlay;
                    user.RememberSubtitleSelections = config.RememberSubtitleSelections;
                    user.SubtitleLanguagePreference = config.SubtitleLanguagePreference;

                    // Only set cast receiver id if it is passed in and it exists in the server config.
                    if (!string.IsNullOrEmpty(config.CastReceiverId)
                        && _serverConfigurationManager.Configuration.CastReceiverApplications.Any(c => string.Equals(c.Id, config.CastReceiverId, StringComparison.Ordinal)))
                    {
                        user.CastReceiverId = config.CastReceiverId;
                    }

                    user.SetPreference(PreferenceKind.OrderedViews, config.OrderedViews);
                    user.SetPreference(PreferenceKind.GroupedFolders, config.GroupedFolders);
                    user.SetPreference(PreferenceKind.MyMediaExcludes, config.MyMediaExcludes);
                    user.SetPreference(PreferenceKind.LatestItemExcludes, config.LatestItemsExcludes);

                    dbContext.Update(user);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc/>
        public async Task UpdatePolicyAsync(Guid userId, UserPolicy policy)
        {
            using (await _userLock.LockAsync(userId).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    var user = UserQuery(dbContext)
                        .AsTracking()
                        .FirstOrDefault(u => u.Id.Equals(userId))
                        ?? throw new ArgumentException("No user exists with given Id!");

                    // The default number of login attempts is 3, but for some god forsaken reason it's sent to the server as "0"
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
                    user.AuthenticationProviderId = policy.AuthenticationProviderId;
                    user.PasswordResetProviderId = policy.PasswordResetProviderId;
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
                    foreach (var policyAccessSchedule in policy.AccessSchedules)
                    {
                        user.AccessSchedules.Add(policyAccessSchedule);
                    }

                    // TODO: fix this at some point
                    user.SetPreference(PreferenceKind.BlockUnratedItems, policy.BlockUnratedItems ?? Array.Empty<UnratedItem>());
                    user.SetPreference(PreferenceKind.BlockedTags, policy.BlockedTags);
                    user.SetPreference(PreferenceKind.AllowedTags, policy.AllowedTags);
                    user.SetPreference(PreferenceKind.EnabledChannels, policy.EnabledChannels);
                    user.SetPreference(PreferenceKind.EnabledDevices, policy.EnabledDevices);
                    user.SetPreference(PreferenceKind.EnabledFolders, policy.EnabledFolders);
                    user.SetPreference(PreferenceKind.EnableContentDeletionFromFolders, policy.EnableContentDeletionFromFolders);

                    dbContext.Update(user);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc/>
        public async Task ClearProfileImageAsync(User user)
        {
            if (user.ProfileImage is null)
            {
                return;
            }

            using (await _userLock.LockAsync(user.Id).ConfigureAwait(false))
            {
                var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
                await using (dbContext.ConfigureAwait(false))
                {
                    dbContext.Remove(user.ProfileImage);
                    await dbContext.SaveChangesAsync().ConfigureAwait(false);
                }

                user.ProfileImage = null;
            }
        }

        internal static void ThrowIfInvalidUsername(string name)
        {
            if (!string.IsNullOrWhiteSpace(name) && ValidUsernameRegex().IsMatch(name))
            {
                return;
            }

            throw new ArgumentException("Usernames can contain unicode symbols, numbers (0-9), dashes (-), underscores (_), apostrophes ('), and periods (.)", nameof(name));
        }

        private IAuthenticationProvider GetAuthenticationProvider(User user)
        {
            return GetAuthenticationProviders(user)[0];
        }

        private IPasswordResetProvider GetPasswordResetProvider(User? user)
        {
            if (user is null)
            {
                return _defaultPasswordResetProvider;
            }

            return GetPasswordResetProviders(user)[0];
        }

        private List<IAuthenticationProvider> GetAuthenticationProviders(User? user)
        {
            var authenticationProviderId = user?.AuthenticationProviderId;

            var providers = _authenticationProviders.Where(i => i.IsEnabled).ToList();

            if (!string.IsNullOrEmpty(authenticationProviderId))
            {
                providers = providers.Where(i => string.Equals(authenticationProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (providers.Count == 0)
            {
                // Assign the user to the InvalidAuthProvider since no configured auth provider was valid/found
                _logger.LogWarning(
                    "User {Username} was found with invalid/missing Authentication Provider {AuthenticationProviderId}. Assigning user to InvalidAuthProvider until this is corrected",
                    user?.Username,
                    user?.AuthenticationProviderId);
                providers = new List<IAuthenticationProvider>
                {
                    _invalidAuthProvider
                };
            }

            return providers;
        }

        private IPasswordResetProvider[] GetPasswordResetProviders(User user)
        {
            var passwordResetProviderId = user.PasswordResetProviderId;
            var providers = _passwordResetProviders.Where(i => i.IsEnabled).ToArray();

            if (!string.IsNullOrEmpty(passwordResetProviderId))
            {
                providers = providers.Where(i =>
                        string.Equals(passwordResetProviderId, i.GetType().FullName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }

            if (providers.Length == 0)
            {
                providers = new IPasswordResetProvider[]
                {
                    _defaultPasswordResetProvider
                };
            }

            return providers;
        }

        private async Task<(string Username, bool Success)> AuthenticateWithProvider(
            IAuthenticationProvider provider,
            string username,
            string password,
            User? resolvedUser)
        {
            try
            {
                ProviderAuthenticationResult authenticationResult;
                using (EnterAuthenticationProviderCallback())
                {
                    authenticationResult = provider is IRequiresResolvedUser requiresResolvedUser
                        ? await requiresResolvedUser.Authenticate(username, password, resolvedUser).ConfigureAwait(false)
                        : await provider.Authenticate(username, password).ConfigureAwait(false);
                }

                if (authenticationResult.Username != username)
                {
                    _logger.LogDebug("Authentication provider provided updated username {1}", authenticationResult.Username);
                    username = authenticationResult.Username;
                }

                return (username, true);
            }
            catch (AuthenticationException ex)
            {
                _logger.LogDebug(ex, "Error authenticating with provider {Provider}", provider.Name);

                return (username, false);
            }
        }

        private async Task UpdateUserInternalAsync(JellyfinDbContext dbContext, User user)
        {
            dbContext.Users.Attach(user);
            var userEntry = dbContext.Entry(user);
            userEntry.State = EntityState.Modified;
            userEntry.Property(value => value.Password).IsModified = false;
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        private static async Task<User> PersistPasswordHashUpgradeAsync(
            JellyfinDbContext dbContext,
            PasswordHashUpgrade upgrade)
        {
            int updatedRows = await dbContext.Users
                .Where(user => user.Id.Equals(upgrade.UserId) && user.Password == upgrade.ExpectedPassword)
                .ExecuteUpdateAsync(properties => properties
                    .SetProperty(user => user.Password, upgrade.ReplacementPassword)
                    .SetProperty(user => user.RowVersion, user => user.RowVersion + 1))
                .ConfigureAwait(false);

            if (updatedRows != 1)
            {
                throw new AuthenticationException("The account credentials changed during authentication.");
            }

            return await UserQuery(dbContext)
                .FirstOrDefaultAsync(user => user.Id.Equals(upgrade.UserId))
                .ConfigureAwait(false)
                ?? throw new AuthenticationException("The account no longer exists.");
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes all members of this class.
        /// </summary>
        /// <param name="disposing">Defines if the class has been cleaned up by a dispose or finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _userLock.Dispose();
                _authenticationDiscoveryMutex.Dispose();
            }
        }

        internal sealed class LockHelper : IDisposable
        {
            private readonly AsyncKeyedLocker<Guid> _userLock = new();
            private readonly AsyncLocal<LockOwnershipFrame?> _ownership = new();
            private readonly IUserLockWaitObserver? _waitObserver;
            private readonly Func<Guid, ValueTask<IDisposable>> _acquireLock;

            private bool _disposed;

            public LockHelper()
                : this(null)
            {
            }

            internal LockHelper(IUserLockWaitObserver? waitObserver)
                : this(waitObserver, null)
            {
            }

            internal LockHelper(
                IUserLockWaitObserver? waitObserver,
                Func<Guid, ValueTask<IDisposable>>? acquireLock)
            {
                _waitObserver = waitObserver;
                _acquireLock = acquireLock ?? (key => _userLock.LockAsync(key, true));
            }

            private enum LockOwnershipState
            {
                Waiting,
                Held,
                Released
            }

            // Kept for source compatibility with the existing internal test surface. Lock
            // ownership decisions use the keyed frame below, never this counter.
            public static AsyncLocal<int> IsNestedLock { get; set; } = new();

            public bool ShouldLock()
            {
                return GetCurrentFrame() is null;
            }

            public ValueTask<IDisposable> LockAsync(Guid key)
            {
                ThrowIfDisposed();
                if (key.Equals(Guid.Empty))
                {
                    throw new ArgumentException("A user lock requires a resolved user ID.", nameof(key));
                }

                LockOwnershipFrame? previousFrame = GetCurrentFrame();
                if (previousFrame is not null)
                {
                    if (previousFrame.State == LockOwnershipState.Waiting)
                    {
                        throw new UserLockOrderException("A user lock cannot be nested while this async flow is waiting for a key.");
                    }

                    if (!previousFrame.Key.Equals(key))
                    {
                        throw new UserLockOrderException("A different user lock cannot be nested inside the current user lock.");
                    }

                    return ReenterHeldKey(previousFrame);
                }

                object topToken = new();
                var frame = new LockOwnershipFrame(key, previousFrame, LockOwnershipState.Waiting, 1, null, topToken);
                _ownership.Value = frame;

                ValueTask<IDisposable> acquisition;
                try
                {
                    acquisition = _acquireLock(key);
                }
                catch
                {
                    RestoreFrame(frame, previousFrame);
                    throw;
                }

                if (acquisition.IsCompleted)
                {
                    try
                    {
                        IDisposable releaser = acquisition.GetAwaiter().GetResult();
                        frame.Releaser = releaser;
                        frame.State = LockOwnershipState.Held;
                        return new ValueTask<IDisposable>(new LockHandle(this, frame, topToken, null));
                    }
                    catch
                    {
                        RestoreFrame(frame, previousFrame);
                        throw;
                    }
                }

                Task<IDisposable> acquisitionTask = acquisition.AsTask();
                if (_waitObserver is not null)
                {
                    try
                    {
                        _waitObserver.OnWaitingForKey(key);
                    }
                    catch
                    {
                        RestoreFrame(frame, previousFrame);
                        _ = ReleaseAfterObserverFailureAsync(acquisitionTask);
                        throw;
                    }
                }

                return new ValueTask<IDisposable>(CompleteAcquisitionAsync(acquisitionTask, frame, topToken));
            }

            private ValueTask<IDisposable> ReenterHeldKey(LockOwnershipFrame frame)
            {
                object? previousTop = frame.TopToken;
                object topToken = new();
                frame.Depth++;
                frame.TopToken = topToken;
                return new ValueTask<IDisposable>(new LockHandle(this, frame, topToken, previousTop));
            }

            private async Task<IDisposable> CompleteAcquisitionAsync(
                Task<IDisposable> acquisitionTask,
                LockOwnershipFrame frame,
                object topToken)
            {
                try
                {
                    IDisposable releaser = await acquisitionTask.ConfigureAwait(false);
                    frame.Releaser = releaser;
                    frame.State = LockOwnershipState.Held;
                    return new LockHandle(this, frame, topToken, null);
                }
                catch
                {
                    RestoreFrame(frame, frame.Parent);
                    throw;
                }
            }

            private static async Task ReleaseAfterObserverFailureAsync(Task<IDisposable> acquisitionTask)
            {
                try
                {
                    IDisposable releaser = await acquisitionTask.ConfigureAwait(false);
                    releaser.Dispose();
                }
                catch (Exception acquisitionFailure)
                {
                    // The observer failure is the caller-visible failure. This cleanup task
                    // observes acquisition errors so they cannot become unobserved exceptions.
                    Trace.TraceError("The user-lock acquisition failed after its wait observer threw: {0}", acquisitionFailure);
                }
            }

            private LockOwnershipFrame? GetCurrentFrame()
            {
                LockOwnershipFrame? current = _ownership.Value;
                while (current is not null && current.State == LockOwnershipState.Released)
                {
                    current = current.Parent;
                }

                if (!ReferenceEquals(current, _ownership.Value))
                {
                    _ownership.Value = current;
                }

                return current;
            }

            private void RestoreFrame(LockOwnershipFrame frame, LockOwnershipFrame? previousFrame)
            {
                frame.State = LockOwnershipState.Released;
                if (ReferenceEquals(_ownership.Value, frame))
                {
                    _ownership.Value = previousFrame;
                }
            }

            private void DisposeHandle(LockOwnershipFrame frame, object topToken, object? previousTop)
            {
                if (frame.State != LockOwnershipState.Held
                    || !ReferenceEquals(_ownership.Value, frame)
                    || !ReferenceEquals(frame.TopToken, topToken))
                {
                    throw new UserLockOrderException("User lock handles must be disposed in key-checked LIFO order.");
                }

                frame.TopToken = previousTop;
                frame.Depth--;
                if (frame.Depth > 0)
                {
                    return;
                }

                frame.State = LockOwnershipState.Released;
                _ownership.Value = frame.Parent;
                frame.Releaser?.Dispose();
                frame.Releaser = null;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _userLock.Dispose();
            }

            private void ThrowIfDisposed()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            private sealed class LockOwnershipFrame
            {
                public LockOwnershipFrame(
                    Guid key,
                    LockOwnershipFrame? parent,
                    LockOwnershipState state,
                    int depth,
                    IDisposable? releaser,
                    object topToken)
                {
                    Key = key;
                    Parent = parent;
                    State = state;
                    Depth = depth;
                    Releaser = releaser;
                    TopToken = topToken;
                }

                public Guid Key { get; }

                public LockOwnershipFrame? Parent { get; }

                public LockOwnershipState State { get; set; }

                public int Depth { get; set; }

                public IDisposable? Releaser { get; set; }

                public object? TopToken { get; set; }
            }

            private sealed class LockHandle : IDisposable
            {
                private readonly LockHelper _owner;
                private readonly LockOwnershipFrame _frame;
                private readonly object _topToken;
                private readonly object? _previousTop;
                private int _disposed;

                public LockHandle(LockHelper owner, LockOwnershipFrame frame, object topToken, object? previousTop)
                {
                    _owner = owner;
                    _frame = frame;
                    _topToken = topToken;
                    _previousTop = previousTop;
                }

                public void Dispose()
                {
                    if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    {
                        return;
                    }

                    try
                    {
                        _owner.DisposeHandle(_frame, _topToken, _previousTop);
                    }
                    catch
                    {
                        Volatile.Write(ref _disposed, 0);
                        throw;
                    }
                }
            }
        }

        internal sealed class UserLockOrderException : InvalidOperationException
        {
            public UserLockOrderException(string message)
                : base(message)
            {
            }
        }

        private sealed class PasswordHashUpgrade
        {
            public required Guid UserId { get; init; }

            public required string ExpectedPassword { get; init; }

            public required string ReplacementPassword { get; init; }
        }

        internal sealed class CredentialMutationException : InvalidOperationException
        {
            public CredentialMutationException(string message)
                : base(message)
            {
            }
        }
    }
}
