// SlopTank modification notice: added or changed by SlopTank on 2026-10-12.
using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Controllers;

internal enum UserImageUploadOutcome
{
    Published,
    UserNotFound,
    Forbidden,
    Contended
}

internal sealed class UserImageUploadCoordinator
{
    private readonly IUserManager _userManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ImageController> _logger;

    public UserImageUploadCoordinator(
        IUserManager userManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<ImageController> logger)
    {
        _userManager = userManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    public async Task<UserImageUploadOutcome> UploadAsync(
        Guid userId,
        string userDirectory,
        string extension,
        string? mimeType,
        Stream imageStream,
        Func<User, bool> canUpdateUser)
    {
        string ownedPath = Path.Combine(userDirectory, "profile-" + Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture) + extension);
        try
        {
            await _providerManager.SaveImage(imageStream, mimeType, ownedPath).ConfigureAwait(false);
        }
        catch (Exception saveException)
        {
            DeleteOwnedArtifact(ownedPath, saveException);
            throw;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var freshUser = _userManager.GetUserById(userId);
            if (freshUser is null)
            {
                DeleteOwnedArtifact(ownedPath);
                return UserImageUploadOutcome.UserNotFound;
            }

            if (!canUpdateUser(freshUser))
            {
                DeleteOwnedArtifact(ownedPath);
                return UserImageUploadOutcome.Forbidden;
            }

            freshUser.ProfileImage = new ImageInfo(ownedPath);
            try
            {
                await _userManager.UpdateUserAsync(freshUser).ConfigureAwait(false);
                return UserImageUploadOutcome.Published;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt == 0)
            {
                _logger.LogDebug(ex, "Retrying user image selection after a concurrent user update for {UserId}.", userId);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogWarning(ex, "User image selection for {UserId} remained contended after one retry.", userId);
                DeleteOwnedArtifact(ownedPath, ex);
                return UserImageUploadOutcome.Contended;
            }
            catch (ResourceNotFoundException)
            {
                DeleteOwnedArtifact(ownedPath);
                return UserImageUploadOutcome.UserNotFound;
            }
            catch (Exception saveException)
            {
                User? selectedUser;
                try
                {
                    selectedUser = _userManager.GetUserById(userId);
                }
                catch (Exception reconciliationException)
                {
                    throw new AggregateException(
                        "The profile image save and selection reconciliation both failed; the request-owned image was retained.",
                        saveException,
                        reconciliationException);
                }

                if (string.Equals(selectedUser?.ProfileImage?.Path, ownedPath, StringComparison.Ordinal))
                {
                    _logger.LogWarning(saveException, "Profile image selection for {UserId} was confirmed after a save error.", userId);
                    return UserImageUploadOutcome.Published;
                }

                DeleteOwnedArtifact(ownedPath, saveException);
                ExceptionDispatchInfo.Capture(saveException).Throw();
                throw new InvalidOperationException("Unreachable after rethrowing the profile image save failure.");
            }
        }

        return UserImageUploadOutcome.Contended;
    }

    private void DeleteOwnedArtifact(string path, Exception? primaryException = null)
    {
        try
        {
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.DeleteFile(path);
            }
        }
        catch (Exception cleanupException)
        {
            throw new AggregateException(
                "The request-owned profile image could not be removed after its selection was refused.",
                primaryException is null ? new[] { cleanupException } : new[] { primaryException, cleanupException });
        }
    }
}
