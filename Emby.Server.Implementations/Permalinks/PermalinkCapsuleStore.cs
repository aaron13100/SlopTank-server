using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Owns portable immutable capsule, event, and root-local anchor publication.
/// </summary>
internal sealed class PermalinkCapsuleStore
{
    private readonly IPermalinkAtomicFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkCapsuleStore"/> class.
    /// </summary>
    public PermalinkCapsuleStore(IPermalinkAtomicFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Computes the immutable capsule placement for a current local binding.
    /// </summary>
    public string GetCapsulePath(
        string rootPath,
        string itemPath,
        string itemKind,
        Guid capsuleId)
    {
        if (File.Exists(itemPath))
        {
            return Path.Combine(
                rootPath,
                ".sloptank",
                "permalink-file-capsules",
                capsuleId.ToString("D"));
        }

        if (!Directory.Exists(itemPath))
        {
            _fileSystem.CreateDirectoryDurable(itemPath);
        }

        return Path.Combine(
            itemPath,
            "." + itemKind.ToLowerInvariant() + ".sloptank-permalink");
    }

    /// <summary>
    /// Publishes or exactly adopts the authority-frozen capsule and anchor record.
    /// </summary>
    public async Task<string> PublishGenesisAsync(
        PermalinkGenesisReservation reservation,
        string currentRoot,
        string currentPath,
        CancellationToken cancellationToken)
    {
        var capsulePath = GetCapsulePath(
            currentRoot,
            currentPath,
            reservation.ItemKind,
            reservation.CapsuleId);
        if (!Directory.Exists(capsulePath))
        {
            var parent = Path.GetDirectoryName(capsulePath)!;
            _fileSystem.CreateDirectoryDurable(parent);
            var temporary = Path.Combine(
                parent,
                ".caller-temp-capsule-" + Guid.NewGuid().ToString("N"));
            try
            {
                _fileSystem.CreateDirectoryDurable(Path.Combine(temporary, "events"));
                await _fileSystem.PublishImmutableAsync(
                    Path.Combine(temporary, "capsule.json"),
                    reservation.HeaderJson,
                    cancellationToken).ConfigureAwait(false);
                var eventDocument = CanonicalJson.Deserialize<PermalinkEventDocument>(
                    reservation.EventJson.Span,
                    "authority genesis event");
                await _fileSystem.PublishImmutableAsync(
                    Path.Combine(
                        temporary,
                        "events",
                        eventDocument.EventId.ToString("D") + ".json"),
                    reservation.EventJson,
                    cancellationToken).ConfigureAwait(false);
                _fileSystem.PublishDirectoryImmutable(temporary, capsulePath);
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
        }

        var anchorPath = GetAnchorPath(currentRoot, reservation.AnchorToken);
        await PublishOrVerifyAsync(
            anchorPath,
            reservation.AnchorJson,
            cancellationToken).ConfigureAwait(false);
        return capsulePath;
    }

    /// <summary>
    /// Appends an exact authority-elected alias event.
    /// </summary>
    public async Task AppendAliasAsync(
        string capsulePath,
        PermalinkAliasClaim claim,
        CancellationToken cancellationToken)
    {
        var eventPath = Path.Combine(
            capsulePath,
            "events",
            claim.EventId.ToString("D") + ".json");
        await PublishOrVerifyAsync(
            eventPath,
            claim.EventJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends an exact authority-authorized content successor.
    /// </summary>
    public async Task AppendContentAsync(
        string capsulePath,
        PermalinkEventDocument successor,
        ReadOnlyMemory<byte> eventJson,
        CancellationToken cancellationToken)
    {
        var eventPath = Path.Combine(
            capsulePath,
            "events",
            successor.EventId.ToString("D") + ".json");
        await PublishOrVerifyAsync(
            eventPath,
            eventJson,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and fail-closed validates a complete capsule.
    /// </summary>
    public async Task<PermalinkCapsuleSnapshot> ReadValidatedAsync(
        string capsulePath,
        Guid expectedCapsuleId,
        string expectedKind,
        CancellationToken cancellationToken)
    {
        var headerPath = Path.Combine(capsulePath, "capsule.json");
        if (!File.Exists(headerPath))
        {
            throw Conflict("capsule-header-missing", $"Capsule header is missing at '{headerPath}'.");
        }

        var headerBytes = await File.ReadAllBytesAsync(headerPath, cancellationToken).ConfigureAwait(false);
        var header = CanonicalJson.Deserialize<PermalinkCapsuleDocument>(headerBytes, headerPath);
        if (header.Type != "sloptank.permalink-capsule")
        {
            throw Conflict("capsule-foreign", $"Capsule '{headerPath}' has foreign type '{header.Type}'.");
        }

        if (header.Version != 1)
        {
            throw Conflict(
                "capsule-unknown-version",
                $"Capsule '{headerPath}' has unknown version {header.Version}.");
        }

        if (header.CapsuleId != expectedCapsuleId
            || !string.Equals(header.ItemKind, expectedKind, StringComparison.Ordinal))
        {
            throw Conflict(
                "capsule-identity-mismatch",
                $"Capsule '{headerPath}' has the wrong id or immutable item kind.");
        }

        var eventsRoot = Path.Combine(capsulePath, "events");
        var eventPaths = Directory.Exists(eventsRoot)
            ? Directory.GetFiles(eventsRoot, "*.json")
            : [];
        if (eventPaths.Length == 0)
        {
            throw Conflict("event-missing", $"Capsule '{capsulePath}' has a missing event lineage.");
        }

        var events = new Dictionary<Guid, PermalinkEventDocument>();
        foreach (var path in eventPaths)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var document = CanonicalJson.Deserialize<PermalinkEventDocument>(bytes, path);
            if (document.Type != "sloptank.permalink-event")
            {
                throw Conflict("event-foreign", $"Event '{path}' has foreign type '{document.Type}'.");
            }

            if (document.Version != 1)
            {
                throw Conflict(
                    "event-unknown-version",
                    $"Event '{path}' has unknown version {document.Version}.");
            }

            if (!events.TryAdd(document.EventId, document))
            {
                throw Conflict(
                    "event-duplicate",
                    $"Capsule '{capsulePath}' contains duplicate event id '{document.EventId}'.");
            }
        }

        ValidateParents(events, capsulePath);
        var contentEvents = events.Values
            .Where(value => value.Kind is "mint" or "identity_seed" or "append-only-discovery")
            .ToArray();
        var heads = contentEvents
            .Where(candidate => !contentEvents.Any(
                other => other.PreviousContentEventId == candidate.EventId))
            .ToArray();
        if (heads.Length != 1)
        {
            throw Conflict(
                "content-head-conflict",
                $"Capsule '{capsulePath}' folds to {heads.Length} content heads.");
        }

        return new PermalinkCapsuleSnapshot(
            header,
            heads[0],
            events.Values.OrderBy(value => value.CreatedAt, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateParents(
        IReadOnlyDictionary<Guid, PermalinkEventDocument> events,
        string capsulePath)
    {
        foreach (var entry in events)
        {
            foreach (var parent in entry.Value.ParentEventIds)
            {
                if (!events.ContainsKey(parent))
                {
                    throw Conflict(
                        "event-parent-missing",
                        $"Capsule '{capsulePath}' event '{entry.Key}' has missing parent '{parent}'.");
                }
            }
        }
    }

    private async Task PublishOrVerifyAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            try
            {
                await _fileSystem.PublishImmutableAsync(
                    path,
                    expected,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PermalinkException exception)
                when (exception.Code == "publish-exclusive")
            {
                // A concurrent writer won; exact-byte verification below decides adoption.
            }
        }

        var actual = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected.Span))
        {
            throw Conflict(
                "immutable-collision",
                $"Immutable permalink path '{path}' exists with foreign bytes.");
        }
    }

    private static string GetAnchorPath(string rootPath, string anchorToken)
    {
        return Path.Combine(
            rootPath,
            ".sloptank",
            "permalink-file-anchors",
            anchorToken + ".json");
    }

    private static PermalinkException Conflict(string code, string message)
    {
        return new PermalinkException(PermalinkErrorKind.Conflict, code, message);
    }
}

/// <summary>
/// Holds the validated folded capsule state.
/// </summary>
internal sealed record PermalinkCapsuleSnapshot(
    PermalinkCapsuleDocument Header,
    PermalinkEventDocument ContentHead,
    IReadOnlyList<PermalinkEventDocument> Events);
