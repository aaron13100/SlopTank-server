// SlopTank modification notice: added or changed by SlopTank on 2026-10-03.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Permalinks;

namespace Emby.Server.Implementations.Permalinks;

/// <summary>
/// Publishes and verifies an adoption's terminal-on-creation journal record.
/// </summary>
/// <remarks>
/// The record is operation.json plus exactly one committed phase, kind "adoption". It is a pure
/// function of <see cref="PermalinkAdoptionEvidence"/>, so a retry after a crash between
/// authorization and publication rebuilds the identical record from the authority's own rows and
/// immutable capsule event and never re-authorizes content. Both files share the
/// successor event's creation time, which must parse: the reconciler logs any operation it does
/// visit with an unparseable timestamp and leaves it fenced, so a malformed value is refused
/// before anything is published. The record is published only after the successor and its
/// authorization are durable, and an existing settled record is verified, never rewritten. An
/// unsettled record an earlier release left behind is completed under its own creation time.
/// </remarks>
internal sealed class PermalinkAdoptionJournalRecord
{
    /// <summary>The journal operation kind that names an adoption.</summary>
    public const string OperationKind = "adoption";

    private readonly PermalinkOperationJournal _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermalinkAdoptionJournalRecord"/> class.
    /// </summary>
    /// <param name="journal">The durable operation journal.</param>
    public PermalinkAdoptionJournalRecord(PermalinkOperationJournal journal)
    {
        _journal = journal;
    }

    /// <summary>
    /// Requires a canonical round-trip RFC 3339 timestamp.
    /// </summary>
    /// <param name="createdAt">The candidate creation timestamp.</param>
    /// <param name="source">What produced the timestamp, for the refusal message.</param>
    /// <exception cref="PermalinkException">Thrown with code adoption-created-at-invalid.</exception>
    public static void RequireCreatedAt(string createdAt, string source)
    {
        if (!DateTimeOffset.TryParseExact(
                createdAt,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            throw new PermalinkException(
                PermalinkErrorKind.Conflict,
                "adoption-created-at-invalid",
                $"The {source} creation time '{createdAt}' is not a canonical RFC 3339 timestamp, "
                + "so no adoption journal record is published from it.");
        }
    }

    /// <summary>
    /// Makes the adoption's journal record exist, settled as committed, without rewriting a record
    /// that is already there.
    /// </summary>
    /// <param name="evidence">The authority evidence the record is derived from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="PermalinkException">
    /// Thrown with code operation-id-reused when a record exists that is not this adoption's, and
    /// adoption-created-at-invalid when the creation time cannot be published.
    /// </exception>
    public async Task EnsureCommittedAsync(
        PermalinkAdoptionEvidence evidence,
        CancellationToken cancellationToken)
    {
        var (existing, settled) = await InspectExistingAsync(evidence, cancellationToken)
            .ConfigureAwait(false);
        if (settled is not null)
        {
            return;
        }

        // An unsettled record an earlier release left behind carries the wall-clock time it was
        // written at. Completing it keeps that time on both files, so the operation and its
        // terminal phase never disagree; a fresh record takes the authority's time.
        var createdAt = existing?.CreatedAt ?? evidence.CreatedAt;
        RequireCreatedAt(createdAt, existing is null ? "authorized successor" : "unsettled adoption record");
        if (existing is null)
        {
            await _journal.WriteOperationAsync(
                Operation(evidence, createdAt),
                cancellationToken).ConfigureAwait(false);
        }

        await _journal.WritePhaseAsync(
            evidence.OperationId,
            PermalinkPhase.Committed,
            CommittedPhase(evidence, createdAt),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates any record already occupying an adoption id without writing to it.
    /// </summary>
    /// <remarks>
    /// Resume calls this before publishing a claimed successor. A colliding record must be
    /// rejected before the retry appends an event or updates a binding, not discovered later
    /// while the terminal phase is being written.
    /// </remarks>
    /// <param name="evidence">The immutable adoption evidence.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The existing settled phase, or null when the id is absent or unsettled.</returns>
    public async Task<PermalinkPhase?> ValidateExistingAsync(
        PermalinkAdoptionEvidence evidence,
        CancellationToken cancellationToken)
    {
        var (_, settled) = await InspectExistingAsync(evidence, cancellationToken)
            .ConfigureAwait(false);
        return settled;
    }

    private async Task<(PermalinkOperationDocument? Existing, PermalinkPhase? Settled)>
        InspectExistingAsync(
            PermalinkAdoptionEvidence evidence,
            CancellationToken cancellationToken)
    {
        // Resume invokes this before any claimed successor or binding publication. Validate the
        // authority timestamp here, rather than only while writing the record, so malformed
        // immutable evidence cannot cause a partial recovery first.
        RequireCreatedAt(evidence.CreatedAt, "authorized successor");
        if (!PermalinkContentAdoption.DeriveOperationId(
                evidence.ItemId,
                evidence.ObservedContentRoot).Equals(evidence.OperationId))
        {
            throw Reused(evidence, "the id is not derived from the item and observed content root");
        }

        var existing = await _journal.ReadAsync(evidence.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            RequireSameAdoption(existing, evidence);
        }

        var settled = _journal.GetSettledPhase(evidence.OperationId);
        if (settled is not null)
        {
            await RequireCommittedAsync(settled, evidence, cancellationToken).ConfigureAwait(false);
        }

        return (existing, settled);
    }

    private static PermalinkOperationDocument Operation(
        PermalinkAdoptionEvidence evidence,
        string createdAt)
    {
        return new PermalinkOperationDocument(
            evidence.OperationId,
            evidence.ItemId,
            evidence.CapsuleId,
            OperationKind,
            evidence.SourcePath,
            null,
            evidence.RecordedContentRoot,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            null,
            null,
            createdAt);
    }

    private static PermalinkOperationPhase CommittedPhase(
        PermalinkAdoptionEvidence evidence,
        string createdAt)
    {
        return new PermalinkOperationPhase(
            evidence.OperationId,
            PermalinkPhase.Committed.Name,
            evidence.ObservedContentRoot,
            createdAt)
        {
            ObservedState =
                "reason=operator-confirmed-adoption"
                + $";recorded={evidence.RecordedContentRoot}"
                + $";observed={evidence.ObservedContentRoot}"
        };
    }

    private static void RequireSameAdoption(
        PermalinkOperationDocument existing,
        PermalinkAdoptionEvidence evidence)
    {
        if (!existing.OperationId.Equals(evidence.OperationId)
            || !existing.ItemId.Equals(evidence.ItemId)
            || !existing.CapsuleId.Equals(evidence.CapsuleId)
            || !string.Equals(existing.Kind, OperationKind, StringComparison.Ordinal)
            || !string.Equals(existing.SourcePath, evidence.SourcePath, StringComparison.Ordinal)
            || !string.Equals(
                existing.OldContentRoot,
                evidence.RecordedContentRoot,
                StringComparison.Ordinal))
        {
            throw Reused(
                evidence,
                "the existing record names a different item, capsule, kind, path or predecessor");
        }
    }

    private async Task RequireCommittedAsync(
        PermalinkPhase settled,
        PermalinkAdoptionEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (settled != PermalinkPhase.Committed)
        {
            throw Reused(evidence, $"the existing record settled as '{settled.Name}', not committed");
        }

        // Records an earlier release published carry the wall-clock time they were written at, not
        // the successor's, so a settled record is verified on what it asserts about the content
        // and never rewritten to match the shared time.
        var phase = await _journal.ReadPhaseAsync(evidence.OperationId, settled, cancellationToken)
            .ConfigureAwait(false);
        if (phase is null
            || !phase.OperationId.Equals(evidence.OperationId)
            || !string.Equals(phase.ContentRoot, evidence.ObservedContentRoot, StringComparison.Ordinal))
        {
            throw Reused(evidence, "the committed phase names a different content root");
        }
    }

    private static PermalinkException Reused(PermalinkAdoptionEvidence evidence, string reason)
    {
        return new PermalinkException(
            PermalinkErrorKind.Conflict,
            "operation-id-reused",
            $"Adoption operation '{evidence.OperationId}' has different input: {reason}.");
    }
}
