// SlopTank modification notice: added or changed by SlopTank on 2026-09-08, 2026-09-09.
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace Jellyfin.Api.Models.ActivityLogDtos;

/// <summary>
/// A bounded request for a locally operated media-organizer warning.
/// </summary>
public sealed class CreateToolAlertRequestDto : IValidatableObject
{
    /// <summary>
    /// The only activity type accepted from the tool-alert endpoint.
    /// </summary>
    public const string AllowedType = "MediaOrganizer";

    /// <summary>
    /// The only severity accepted from the tool-alert endpoint.
    /// </summary>
    public const string AllowedSeverity = "Warning";

    /// <summary>
    /// Maximum alert name length.
    /// </summary>
    public const int MaxNameLength = 256;

    /// <summary>
    /// Maximum alert overview length.
    /// </summary>
    public const int MaxOverviewLength = 512;

    /// <summary>
    /// Gets or sets the owner-visible alert name.
    /// </summary>
    [Required]
    [StringLength(MaxNameLength, MinimumLength = 1)]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets optional owner-visible detail.
    /// </summary>
    [StringLength(MaxOverviewLength)]
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the constrained activity type.
    /// </summary>
    [Required]
    [RegularExpression("^" + AllowedType + "$")]
    public required string Type { get; set; }

    /// <summary>
    /// Gets or sets the constrained warning severity.
    /// </summary>
    [Required]
    [RegularExpression("^" + AllowedSeverity + "$")]
    public required string Severity { get; set; }

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name is not null && !Name.Equals(Name.Trim(), StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "Name must not have leading or trailing whitespace.",
                [nameof(Name)]);
        }

        if (Name is not null && HasUnsafeCharacters(Name, allowLineWhitespace: false))
        {
            yield return new ValidationResult(
                "Name must not contain control or formatting characters.",
                [nameof(Name)]);
        }

        if (Overview is not null && HasUnsafeCharacters(Overview, allowLineWhitespace: true))
        {
            yield return new ValidationResult(
                "Overview must not contain control or formatting characters other than line whitespace.",
                [nameof(Overview)]);
        }
    }

    private static bool HasUnsafeCharacters(string value, bool allowLineWhitespace)
    {
        foreach (var character in value)
        {
            if (allowLineWhitespace && character is '\r' or '\n' or '\t')
            {
                continue;
            }

            if (char.IsControl(character)
                || char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }
}
