using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;

namespace Jellyfin.Server.Implementations.Events.Consumers.Session;

/// <summary>
/// Prioritizes missing trickplay images for an actively playing video.
/// </summary>
public sealed class PlaybackStartTrickplayGenerator : IEventConsumer<PlaybackStartEventArgs>
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITrickplayManager _trickplayManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartTrickplayGenerator"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="trickplayManager">The trickplay manager.</param>
    public PlaybackStartTrickplayGenerator(
        ILibraryManager libraryManager,
        ITrickplayManager trickplayManager)
    {
        _libraryManager = libraryManager;
        _trickplayManager = trickplayManager;
    }

    /// <inheritdoc />
    public Task OnEvent(PlaybackStartEventArgs eventArgs)
    {
        if (eventArgs.Item is Video video && !video.IsThemeMedia)
        {
            _trickplayManager.QueueTrickplayGenerationForPlayback(
                video,
                _libraryManager.GetLibraryOptions(video));
        }

        return Task.CompletedTask;
    }
}
