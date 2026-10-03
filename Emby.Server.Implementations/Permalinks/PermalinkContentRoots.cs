// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
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

    /// <summary>Gets every configured root path in longest-prefix resolution order.</summary>
    public IReadOnlyList<string> ConfiguredRoots => Array.AsReadOnly(_roots);

    /// <summary>
    /// Finds the configured content root a path lives under.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The configured root (full path).</returns>
    /// <exception cref="PermalinkException">Ineligible <c>content-root-missing</c> when no configured root contains the path.</exception>
    public string Resolve(string path)
    {
        if (TryResolve(path, out var root))
        {
            return root;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "content-root-missing",
            $"Path '{path}' is outside every configured permalink content root.");
    }

    /// <summary>Tries to find the configured content root a path lives under.</summary>
    /// <param name="path">The path.</param>
    /// <param name="root">The configured full root when the path is managed.</param>
    /// <returns>True when a configured root contains the path.</returns>
    public bool TryResolve(string path, out string root)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var candidateRoot in _roots)
        {
            var relative = Path.GetRelativePath(candidateRoot, fullPath);
            if (relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative))
            {
                root = candidateRoot;
                return true;
            }
        }

        root = string.Empty;
        return false;
    }
}
