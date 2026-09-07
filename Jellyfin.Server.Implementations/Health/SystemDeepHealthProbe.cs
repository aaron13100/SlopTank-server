using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller.Health;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Health;

/// <summary>
/// Connects the bounded evaluator to the server's existing tracked state.
/// </summary>
public sealed class SystemDeepHealthProbe : IDeepHealthProbe
{
    private readonly IDbContextFactory<JellyfinDbContext> _database;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemDeepHealthProbe"/> class.
    /// </summary>
    /// <param name="database">Factory for the configured application database.</param>
    /// <param name="libraryManager">Configured library metadata source.</param>
    /// <param name="mediaEncoder">Configured media encoder.</param>
    /// <param name="sessionManager">Tracked session and transcode state.</param>
    public SystemDeepHealthProbe(
        IDbContextFactory<JellyfinDbContext> database,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        ISessionManager sessionManager)
    {
        _database = database;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public async ValueTask QueryDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var context = await _database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        _ = await context.Users
            .AsNoTracking()
            .Select(user => user.Id)
            .Take(1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetLibraryRoots()
        => _libraryManager.GetVirtualFolders()
            .SelectMany(folder => folder.Locations)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

    /// <inheritdoc />
    public string GetFfmpegPath() => _mediaEncoder.EncoderPath;

    /// <inheritdoc />
    public bool HasStalledTranscode(DateTime thresholdUtc)
        => _sessionManager.HasTranscodeAtZeroProgressSince(thresholdUtc);
}
