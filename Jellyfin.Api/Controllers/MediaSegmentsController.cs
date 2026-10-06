// SlopTank modification notice: added or changed by SlopTank on 2026-10-07.
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.MediaSegmentDtos;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Media Segments api.
/// </summary>
[Authorize]
[Tags("MediaSegment")]
public class MediaSegmentsController : BaseJellyfinApiController
{
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly ILibraryManager _libraryManager;
    private readonly EpisodeContentEndPolicy _episodeContentEndPolicy;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSegmentsController"/> class.
    /// </summary>
    /// <param name="mediaSegmentManager">MediaSegments Manager.</param>
    /// <param name="libraryManager">The Library manager.</param>
    /// <param name="episodeContentEndPolicy">The episode content-end boundary policy.</param>
    public MediaSegmentsController(IMediaSegmentManager mediaSegmentManager, ILibraryManager libraryManager, EpisodeContentEndPolicy episodeContentEndPolicy)
    {
        _mediaSegmentManager = mediaSegmentManager;
        _libraryManager = libraryManager;
        _episodeContentEndPolicy = episodeContentEndPolicy;
    }

    /// <summary>
    /// Gets all media segments based on an itemId.
    /// </summary>
    /// <param name="itemId">The ItemId.</param>
    /// <param name="includeSegmentTypes">Optional filter of requested segment types.</param>
    /// <returns>A list of media segment objects related to the requested itemId.</returns>
    [HttpGet("{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<MediaSegmentDto>>> GetItemSegments(
        [FromRoute, Required] Guid itemId,
        [FromQuery] IEnumerable<MediaSegmentType>? includeSegmentTypes = null)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(item);
        var items = await _mediaSegmentManager.GetSegmentsAsync(item, includeSegmentTypes, libraryOptions).ConfigureAwait(false);
        return Ok(new QueryResult<MediaSegmentDto>(items.ToArray()));
    }

    /// <summary>
    /// Gets the canonical content-completion boundary of an episode: the start of a
    /// terminal outro segment when one exists, otherwise the MaxResumePct fallback.
    /// </summary>
    /// <param name="itemId">The ItemId.</param>
    /// <returns>The episode content-end boundary, or 404 when the item is missing or not an episode.</returns>
    [HttpGet("{itemId}/EpisodeContentEnd")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<EpisodeContentEndDto> GetEpisodeContentEnd([FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is not Episode episode)
        {
            return NotFound();
        }

        var contentEnd = _episodeContentEndPolicy.GetEpisodeContentEnd(episode);
        if (contentEnd is null)
        {
            return Ok(new EpisodeContentEndDto
            {
                ItemId = itemId,
                ContentEndTicks = null,
                Source = null
            });
        }

        return Ok(new EpisodeContentEndDto
        {
            ItemId = itemId,
            ContentEndTicks = contentEnd.ContentEndTicks,
            Source = contentEnd.Source.ToString()
        });
    }
}
