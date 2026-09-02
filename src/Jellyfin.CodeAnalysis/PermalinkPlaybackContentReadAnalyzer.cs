using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Jellyfin.CodeAnalysis;

/// <summary>
/// Analyzer that keeps permalink playback resolution free of media content reads.
/// </summary>
/// <remarks>
/// Opening a shared watch link must cost the same for a 300 MB episode and a
/// 20 GB film: the playback path proves a source is the exact object its lease
/// was issued against by stat, never by reading bytes. That invariant was once
/// broken silently, because the work it cost produced an artifact nothing
/// consumed, so no behaviour changed and no test failed. Behavioural tests
/// cannot police its return on their own: a reintroduced read that bypasses the
/// instrumented content-read meter leaves that guard green, and an absolute
/// latency budget loose enough to survive a loaded build box is far too loose to
/// notice a single extra pass over the file. A compile-time rule makes the
/// regression impossible to merge, so this fails the build rather than a test.
/// It is deliberately paired with, not a replacement for,
/// PlaybackRedemption_CostDoesNotScaleWithMediaSize: this rule is scoped to the
/// types named below, so it cannot see byte-proportional work introduced
/// elsewhere on the redemption path, which is exactly what that test covers.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PermalinkPlaybackContentReadAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Diagnostic descriptor for a media content read on the permalink playback path.
    /// </summary>
    public static readonly DiagnosticDescriptor PlaybackReadsMediaContent = new(
        id: "JF0002",
        title: "Permalink playback resolution must not read media content",
        messageFormat: "'{0}' reads media content in '{1}'. Opening a watch link must prove source identity by stat (PermalinkObjectIdentity), never by reading bytes, so its cost stays independent of media size.",
        category: "Performance",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Types on the permalink playback path must not copy or read media files. Identity is established from the filesystem change token, which is constant cost.");

    /// <summary>The types whose request path must never touch media bytes.</summary>
    private static readonly ImmutableHashSet<string> GuardedTypes = ImmutableHashSet.Create(
        "PermalinkPlaybackPlanStore",
        "PermalinkPlaybackStateStore",
        "PermalinkResolutionService",
        "PermalinkObjectIdentity");

    /// <summary>Members that move or read file contents end to end.</summary>
    private static readonly ImmutableHashSet<string> ContentReadMembers = ImmutableHashSet.Create(
        "Copy",
        "OpenRead",
        "OpenHandle",
        "ReadAllBytes",
        "ReadAllBytesAsync",
        "ReadAllText",
        "ReadAllTextAsync",
        "ReadAllLines",
        "ReadAllLinesAsync",
        "ReadLines",
        "HashData",
        "HashDataAsync",
        "ComputeHash",
        "ComputeHashAsync");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [PlaybackReadsMediaContent];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static string? GuardedTypeFor(SyntaxNode node)
    {
        // Every enclosing type is checked, not just the innermost one. The guarded
        // stores declare nested records, so a read written inside one of those would
        // otherwise resolve to the nested name, match nothing, and escape the rule.
        foreach (var declaration in node.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            var name = declaration.Identifier.ValueText;
            if (GuardedTypes.Contains(name))
            {
                return name;
            }
        }

        return null;
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (GuardedTypeFor(context.Node) is not { } guardedType)
        {
            return;
        }

        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
        {
            return;
        }

        if (!ContentReadMembers.Contains(method.Name))
        {
            return;
        }

        var owner = method.ContainingType?.ToDisplayString();
        if (owner is not "System.IO.File" && owner?.StartsWith("System.Security.Cryptography", System.StringComparison.Ordinal) != true)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            PlaybackReadsMediaContent,
            invocation.GetLocation(),
            $"{owner}.{method.Name}",
            guardedType));
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (GuardedTypeFor(context.Node) is not { } guardedType)
        {
            return;
        }

        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var created = context.SemanticModel.GetSymbolInfo(creation).Symbol?.ContainingType?.ToDisplayString();
        if (created is not "System.IO.FileStream")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            PlaybackReadsMediaContent,
            creation.GetLocation(),
            "System.IO.FileStream",
            guardedType));
    }
}
