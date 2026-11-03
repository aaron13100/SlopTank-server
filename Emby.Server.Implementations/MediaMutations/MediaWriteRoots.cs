// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.MediaMutations;

/// <summary>
/// Reads the roots that the existing guarded media writer may mutate.
/// </summary>
internal sealed class MediaWriteRoots
{
    private readonly string[] _configuredRoots;
    private readonly Lazy<ILibraryManager>? _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaWriteRoots"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="libraryManager">The library manager, resolved only when library roots are needed.</param>
    public MediaWriteRoots(IConfiguration configuration, Lazy<ILibraryManager>? libraryManager = null)
    {
        _configuredRoots = configuration.GetSection("Permalinks:ContentRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Reads explicitly configured roots, or current physical library locations when none are configured.
    /// </summary>
    /// <returns>Normalized allowed roots ordered from longest to shortest.</returns>
    public string[] ReadRoots()
    {
        if (_configuredRoots.Length > 0)
        {
            return _configuredRoots;
        }

        if (_libraryManager is null)
        {
            return Array.Empty<string>();
        }

        return _libraryManager.Value.GetVirtualFolders(includeRefreshState: false)
            .Where(folder => folder is not null)
            .SelectMany(folder => folder.Locations ?? Array.Empty<string>())
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .Select(location => Path.GetFullPath(location!))
            .OrderByDescending(location => location.Length)
            .ToArray();
    }
}
