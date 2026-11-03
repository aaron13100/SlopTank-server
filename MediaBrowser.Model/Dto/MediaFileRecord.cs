// SlopTank modification notice: added or changed by SlopTank on 2026-11-03.
using System;

namespace MediaBrowser.Model.Dto;

/// <summary>A stored media record used to preserve metadata during file preparation.</summary>
/// <param name="Id">The existing library record ID.</param>
/// <param name="Path">The current filename association in the database.</param>
public sealed record MediaFileRecord(Guid Id, string Path);
