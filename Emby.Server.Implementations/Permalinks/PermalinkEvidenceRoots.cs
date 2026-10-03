// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Permalinks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Resolves paths under configured <c>Permalinks:EvidenceRoots</c>. Evidence roots participate in
/// root attestation and absence proofs, but are deliberately separate from content roots so files
/// retained for recovery cannot become eligible media paths.
/// </summary>
internal sealed class PermalinkEvidenceRoots
{
    private readonly string[] _roots;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkEvidenceRoots"/> class.
    /// </summary>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="contentRoots">The separately configured content roots.</param>
    /// <param name="objects">The physical-path inspector used to reject aliased overlap.</param>
    public PermalinkEvidenceRoots(
        IConfiguration configuration,
        PermalinkContentRoots contentRoots,
        IPermalinkPathObjects objects)
    {
        _roots = configuration.GetSection("Permalinks:EvidenceRoots")
            .GetChildren()
            .Select(section => section.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.GetFullPath(value!))
            .OrderByDescending(value => value.Length)
            .ToArray();

        foreach (var evidenceRoot in _roots)
        {
            foreach (var contentRoot in contentRoots.ConfiguredRoots)
            {
                RefuseOverlap(contentRoot, evidenceRoot);
                var canonicalContent = objects.CanonicalizeDirectory(contentRoot);
                var canonicalEvidence = objects.CanonicalizeDirectory(evidenceRoot);
                if (canonicalContent is not null && canonicalEvidence is not null)
                {
                    RefuseOverlap(canonicalContent, canonicalEvidence);
                }
            }
        }
    }

    /// <summary>Gets every configured evidence root in longest-prefix resolution order.</summary>
    public IReadOnlyList<string> ConfiguredRoots => Array.AsReadOnly(_roots);

    /// <summary>Finds the configured evidence root a path lives under.</summary>
    /// <param name="path">The evidence path.</param>
    /// <returns>The configured evidence root (full path).</returns>
    /// <exception cref="PermalinkException">Ineligible <c>evidence-root-missing</c> when no configured root contains the path.</exception>
    public string Resolve(string path)
    {
        if (TryResolve(path, out var root))
        {
            return root;
        }

        throw new PermalinkException(
            PermalinkErrorKind.Ineligible,
            "evidence-root-missing",
            $"Path '{path}' is outside every configured permalink evidence root.");
    }

    /// <summary>Tries to find the configured evidence root a path lives under.</summary>
    /// <param name="path">The evidence path.</param>
    /// <param name="root">The configured full root when the path is managed evidence.</param>
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

    private static void RefuseOverlap(string contentRoot, string evidenceRoot)
    {
        if (Contains(contentRoot, evidenceRoot) || Contains(evidenceRoot, contentRoot))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "permalink-root-role-overlap",
                $"Content root '{contentRoot}' and evidence root '{evidenceRoot}' overlap. Root roles must be disjoint.");
        }
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
