; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules
Rule ID | Category    | Severity | Notes
--------|-------------|----------|-------
JF0002  | Performance | Error    | Permalink playback resolution must not read media content

### Changed Rules
Rule ID | New Category | New Severity | Old Category | Old Severity | Notes
--------|--------------|--------------|--------------|--------------|-------
JF0001  | Usage        | Error        | Usage        | Warning      | Source has enforced Error since the rule shipped; the shipped release file recorded Warning. Recording the real severity rather than weakening the rule to match a stale note.
