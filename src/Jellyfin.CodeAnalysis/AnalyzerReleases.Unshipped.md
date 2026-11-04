; SlopTank modification notice: added or changed by SlopTank on 2026-09-02, 2026-09-09, 2026-09-10, 2026-10-14, 2026-10-31, 2026-11-04.
; Unshipped analyzer release
; https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules
Rule ID | Category    | Severity | Notes
--------|-------------|----------|-------

### Changed Rules
Rule ID | New Category | New Severity | Old Category | Old Severity | Notes
--------|--------------|--------------|--------------|--------------|-------
JF0001  | Usage        | Error        | Usage        | Warning      | Source has enforced Error since the rule shipped; the shipped release file recorded Warning. Recording the real severity rather than weakening the rule to match a stale note.
