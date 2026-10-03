// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// The configured <c>Permalinks:ContentRoots</c> and the one rule that maps a path to the root it
/// lives under: the longest configured root that contains it.
/// </summary>
internal sealed class PermalinkContentRoots
{
    private readonly string[] _roots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkContentRoots"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    public PermalinkContentRoots(IConfiguration configuration)
    {
        _roots = configuration.GetSection("Permalinks:ContentRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    /// <summary>
    /// Finds the configured content root a path lives under.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The configured root (full path).</returns>
    /// <exception cref="PermalinkException">Ineligible <c>content-root-missing</c> when no configured root contains the path.</exception>
    public string Resolve(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var root in _roots)
        {
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                return root;
            }
        }

        throw new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "content-root-missing",
            $"Path '{path}' is outside every configured permalink content root.");
    }
}
