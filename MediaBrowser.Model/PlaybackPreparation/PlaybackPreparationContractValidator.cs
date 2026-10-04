// SlopTank modification notice: added or changed by SlopTank on 2026-10-04.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MediaBrowser.Model.PlaybackPreparation;

/// <summary>
/// Fail-closed validation for the playback-preparation wire contract.
/// </summary>
public static class PlaybackPreparationContractValidator
{
    private const int MaxPathBytes = 4096;
    private const int MaxMediaSignatureBytes = 1024;
    private const int MaxReasonCodeBytes = 64;
    private const int MaxSuffixBytes = 32;
    private const int MaxLanguageBytes = 64;
    private const int MaxBitrateBytes = 32;
    private const int MaxAudioTracks = 32;
    private const int MaxMeasuredIndexes = 32;

    /// <summary>Validates a planner request and returns every contract error.</summary>
    /// <param name="request">The request to validate.</param>
    /// <returns>The validation errors, or an empty collection when valid.</returns>
    public static IReadOnlyList<string> ValidateRequest(PlaybackPreparationRequest? request)
    {
        if (request is null)
        {
            return ["request is required."];
        }

        var errors = new List<string>();
        ValidateEnvelope(request.ContractType, request.ContractVersion, request.PlannerAlgorithmVersion, errors);
        ValidateUuid(request.RequestId, "requestId", errors);
        ValidateNonce(request.Nonce, errors);
        ValidateUuid(request.ItemId, "itemId", errors);
        ValidatePath(request.Path, errors);
        ValidateString(request.MediaSignature, "mediaSignature", MaxMediaSignatureBytes, nonempty: true, errors);
        return errors;
    }

    /// <summary>Validates a planner result and its echoed request binding.</summary>
    /// <param name="request">The request sent to the planner.</param>
    /// <param name="result">The result returned by the planner.</param>
    /// <returns>The validation errors, or an empty collection when valid.</returns>
    public static IReadOnlyList<string> ValidateResult(
        PlaybackPreparationRequest? request,
        PlaybackPreparationResult? result)
    {
        var errors = new List<string>();
        errors.AddRange(ValidateRequest(request));
        if (result is null)
        {
            errors.Add("result is required.");
            return errors;
        }

        ValidateEnvelope(result.ContractType, result.ContractVersion, result.PlannerAlgorithmVersion, errors);
        ValidateUuid(result.RequestId, "requestId", errors);
        ValidateNonce(result.Nonce, errors);
        ValidateUuid(result.ItemId, "itemId", errors);
        ValidatePath(result.Path, errors);
        ValidateString(result.MediaSignature, "mediaSignature", MaxMediaSignatureBytes, nonempty: true, errors);
        ValidateString(result.Outcome, "outcome", 64, nonempty: true, errors);
        ValidateString(result.ReasonCode, "reasonCode", MaxReasonCodeBytes, nonempty: true, errors);

        if (request is not null)
        {
            ValidateEcho(request.RequestId, result.RequestId, "requestId", errors);
            ValidateEcho(request.Nonce, result.Nonce, "nonce", errors);
            ValidateEcho(request.ItemId, result.ItemId, "itemId", errors);
            ValidateEcho(request.Path, result.Path, "path", errors);
            ValidateEcho(request.MediaSignature, result.MediaSignature, "mediaSignature", errors);
            ValidateEcho(request.ContractType, result.ContractType, "contractType", errors);
            if (request.ContractVersion != result.ContractVersion)
            {
                errors.Add("contractVersion does not match the request.");
            }

            ValidateEcho(request.PlannerAlgorithmVersion, result.PlannerAlgorithmVersion, "plannerAlgorithmVersion", errors);
        }

        if (!IsKnownOutcome(result.Outcome))
        {
            errors.Add("outcome is unknown.");
            return errors;
        }

        if (!IsKnownReasonCode(result.Outcome!, result.ReasonCode))
        {
            errors.Add("reasonCode is unknown or invalid for outcome.");
        }

        var planRequired = result.Outcome is
            PlaybackPreparationContract.CompatibleOutcome
            or PlaybackPreparationContract.NeedsPreparationOutcome;
        if (planRequired != (result.Plan is not null))
        {
            errors.Add(planRequired
                ? "planned outcome is missing its plan."
                : "non-planned outcome contains a plan.");
        }

        if (result.Plan is not null)
        {
            ValidatePlan(result.Plan, result.Outcome == PlaybackPreparationContract.NeedsPreparationOutcome, errors);
        }

        return errors;
    }

    /// <summary>Tries to validate a planner request.</summary>
    /// <param name="request">The request to validate.</param>
    /// <param name="error">The first validation error, if any.</param>
    /// <returns><see langword="true"/> when the request is valid.</returns>
    public static bool TryValidateRequest(PlaybackPreparationRequest? request, out string? error)
    {
        var errors = ValidateRequest(request);
        error = errors.FirstOrDefault();
        return errors.Count == 0;
    }

    /// <summary>Tries to validate a planner result.</summary>
    /// <param name="request">The request sent to the planner.</param>
    /// <param name="result">The result returned by the planner.</param>
    /// <param name="error">The first validation error, if any.</param>
    /// <returns><see langword="true"/> when the result is valid.</returns>
    public static bool TryValidateResult(
        PlaybackPreparationRequest? request,
        PlaybackPreparationResult? result,
        out string? error)
    {
        var errors = ValidateResult(request, result);
        error = errors.FirstOrDefault();
        return errors.Count == 0;
    }

    private static void ValidateEnvelope(
        string? contractType,
        int contractVersion,
        string? plannerAlgorithmVersion,
        ICollection<string> errors)
    {
        if (!string.Equals(contractType, PlaybackPreparationContract.ContractType, StringComparison.Ordinal))
        {
            errors.Add("contractType is unknown.");
        }

        if (contractVersion != PlaybackPreparationContract.ContractVersion)
        {
            errors.Add("contractVersion is unsupported.");
        }

        if (!string.Equals(
                plannerAlgorithmVersion,
                PlaybackPreparationContract.PlannerAlgorithmVersion,
                StringComparison.Ordinal))
        {
            errors.Add("plannerAlgorithmVersion is unsupported.");
        }
    }

    private static void ValidateUuid(string? value, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(value)
            || !Guid.TryParse(value, out var parsed)
            || parsed.Equals(Guid.Empty)
            || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
        {
            errors.Add(fieldName + " must be a canonical non-nil UUID.");
        }
    }

    private static void ValidateNonce(string? nonce, ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(nonce)
            || nonce.Length is < 32 or > 256
            || nonce.Length % 2 != 0
            || nonce.Any(character => !IsLowerHexDigit(character))
            || nonce.All(character => character == '0'))
        {
            errors.Add("nonce must contain at least 128 random bits.");
        }
    }

    private static void ValidatePath(string? path, ICollection<string> errors)
    {
        ValidateString(path, "path", MaxPathBytes, nonempty: true, errors);
        if (!string.IsNullOrEmpty(path)
            && !path.Any(char.IsControl)
            && !Path.IsPathFullyQualified(path))
        {
            errors.Add("path must be absolute.");
        }
    }

    private static void ValidateString(
        string? value,
        string fieldName,
        int maxBytes,
        bool nonempty,
        ICollection<string> errors)
    {
        if (value is null || (nonempty && value.Length == 0))
        {
            errors.Add(fieldName + " must be a string.");
            return;
        }

        if (ContainsUnpairedSurrogate(value))
        {
            errors.Add(fieldName + " must be valid UTF-8.");
        }
        else if (global::System.Text.Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            errors.Add(fieldName + " exceeds its maximum size.");
        }
    }

    private static void ValidateEcho<T>(T expected, T actual, string fieldName, ICollection<string> errors)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            errors.Add(fieldName + " does not match the request.");
        }
    }

    private static void ValidatePlan(
        PlaybackPreparationPlan plan,
        bool requiresWork,
        ICollection<string> errors)
    {
        ValidateString(plan.TargetSuffix, "targetSuffix", MaxSuffixBytes, nonempty: true, errors);
        if (!string.IsNullOrEmpty(plan.TargetSuffix) && !IsSimpleSuffix(plan.TargetSuffix))
        {
            errors.Add("targetSuffix must be a simple filename suffix.");
        }

        if (plan.AddedAudio is null || plan.LoudnessMeasuredSourceIndexes is null)
        {
            errors.Add("plan is missing required arrays.");
            return;
        }

        if (!plan.NeedsVideo.HasValue
            || !plan.NeedsAudio.HasValue
            || !plan.NeedsContainer.HasValue
            || !plan.NeedsFaststart.HasValue)
        {
            errors.Add("plan is missing required boolean fields.");
        }

        if (plan.AddedAudio.Length > MaxAudioTracks)
        {
            errors.Add("addedAudio must be a bounded list.");
        }

        foreach (var track in plan.AddedAudio)
        {
            if (track is null)
            {
                errors.Add("audio plan entry must be an object.");
                continue;
            }

            ValidateAudioTrack(track, errors);
        }

        ValidateMeasuredIndexes(plan.LoudnessMeasuredSourceIndexes, errors);

        var requestsWork = plan.NeedsVideo == true
            || plan.NeedsAudio == true
            || plan.NeedsContainer == true
            || plan.NeedsFaststart == true
            || plan.AddedAudio.Length > 0;
        if (requiresWork != requestsWork)
        {
            errors.Add(requiresWork
                ? "needs_preparation plan must request work."
                : "compatible plan must request no work.");
        }
    }

    private static void ValidateAudioTrack(PlaybackPreparationAudioTrack track, ICollection<string> errors)
    {
        if (!track.SourceIndex.HasValue || track.SourceIndex < 0)
        {
            errors.Add("audio sourceIndex must be non-negative.");
        }

        ValidateString(track.Language, "audio language", MaxLanguageBytes, nonempty: false, errors);
        ValidateString(track.Bitrate, "audio bitrate", MaxBitrateBytes, nonempty: false, errors);
        if (!track.Channels.HasValue || track.Channels <= 0)
        {
            errors.Add("audio channels must be positive.");
        }

        if (!track.MakeDefault.HasValue
            || !track.IsCommentary.HasValue
            || !track.ApplyLoudnorm.HasValue)
        {
            errors.Add("audio plan entry is incomplete.");
        }
    }

    private static void ValidateMeasuredIndexes(int[] indexes, ICollection<string> errors)
    {
        if (indexes.Length > MaxMeasuredIndexes)
        {
            errors.Add("loudness indexes must be a bounded list.");
        }

        for (var index = 0; index < indexes.Length; index++)
        {
            if (indexes[index] < 0)
            {
                errors.Add("loudness indexes must be non-negative.");
            }

            if (index > 0 && indexes[index] <= indexes[index - 1])
            {
                errors.Add("loudness indexes must be sorted and unique.");
            }
        }
    }

    private static bool IsKnownOutcome(string? outcome)
    {
        return outcome is
            PlaybackPreparationContract.CompatibleOutcome
            or PlaybackPreparationContract.NeedsPreparationOutcome
            or PlaybackPreparationContract.UnprobeableOutcome
            or PlaybackPreparationContract.RefusedOutcome
            or PlaybackPreparationContract.ErrorOutcome;
    }

    private static bool IsKnownReasonCode(string outcome, string? reasonCode)
    {
        return outcome switch
        {
            PlaybackPreparationContract.CompatibleOutcome => reasonCode == "already_compatible",
            PlaybackPreparationContract.NeedsPreparationOutcome => reasonCode == "work_required",
            PlaybackPreparationContract.UnprobeableOutcome => reasonCode == "planner_unprobeable",
            PlaybackPreparationContract.RefusedOutcome => reasonCode is "contract_refused" or "authority_refused",
            PlaybackPreparationContract.ErrorOutcome => reasonCode == "planner_error",
            _ => false
        };
    }

    private static bool IsSimpleSuffix(string value)
    {
        if (value.Length is < 2 or > 17 || value[0] != '.')
        {
            return false;
        }

        return value.Skip(1).All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9');
    }

    private static bool IsLowerHexDigit(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f';
    }

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (char.IsHighSurrogate(value[index])
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                index++;
                continue;
            }

            return true;
        }

        return false;
    }
}
