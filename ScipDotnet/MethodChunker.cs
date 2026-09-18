using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScipDotnet;

/// <summary>
/// Turns a method or constructor declaration into its vector inputs and structural facts.
/// </summary>
/// <remarks>
/// Aspects, one node per method downstream: the SIGNATURE (header without body) so that
/// "same API, different logic" is searchable on its own; the BODY (statements with the
/// header stripped) so that a rename moves the body vector by exactly zero; and, for bodies
/// longer than <see cref="BlockThresholdLines"/>, the logical BLOCKS — try/catch/finally
/// sections, if-branches, loop bodies, local functions — so a short method can match an
/// identical block buried inside a long one.
/// <para>
/// Three mechanical rules decide which blocks stand alone (syntactic blocks are not
/// automatically logical units): a block under <see cref="BlockFloorLines"/> merges into
/// its parent; a block covering at least <see cref="DominanceShare"/> of the body IS the
/// body and gets no separate vector; a catch made only of throw/log statements is never a
/// block. Text is normalized the same way for every aspect — comments removed, whitespace
/// collapsed — so equal logic hashes equal regardless of layout.
/// </para>
/// <para>
/// Structural facts are plain numbers on every method, thresholds live in queries:
/// body line count, deepest statement nesting, and cyclomatic complexity as 1 plus the
/// branching nodes (if, conditional, loops, catch, case labels and switch arms, &amp;&amp;, ||,
/// ??, ??=).
/// </para>
/// </remarks>
public static class MethodChunker
{
    /// <summary>Bodies longer than this many lines are split into blocks.</summary>
    public const int BlockThresholdLines = 40;

    /// <summary>Blocks shorter than this many lines merge into their parent.</summary>
    public const int BlockFloorLines = 5;

    /// <summary>A block covering this share of the body or more is the body itself.</summary>
    public const double DominanceShare = 0.8;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Collects the signature, the body (with structural facts) and the blocks of a callable.</summary>
    /// <param name="node">The method or constructor declaration.</param>
    /// <returns>The drafts in aspect order; empty for a declaration without a body and without a usable header.</returns>
    public static IReadOnlyList<ChunkDraft> Collect(BaseMethodDeclarationSyntax node)
    {
        var drafts = new List<ChunkDraft>();
        SyntaxNode? body = node.Body ?? (SyntaxNode?)node.ExpressionBody?.Expression;

        var headerEnd = body is null ? node.Span.End : body.SpanStart;
        var headerText = Normalize(node.WithBody(null).WithExpressionBody(null).WithAttributeLists(default))
            .TrimEnd(';', ' ');
        if (headerText.Length > 0)
        {
            drafts.Add(new ChunkDraft("signature", LineOf(node, node.SpanStart), LineOf(node, headerEnd - 1), headerText, 0, 0, 0, 0));
        }

        if (body is null)
        {
            return drafts;
        }

        var bodyText = body is BlockSyntax block
            ? string.Join(" ", block.Statements.Select(Normalize))
            : Normalize(body);
        if (bodyText.Length == 0)
        {
            return drafts;
        }

        var bodyLines = LineCount(body);
        drafts.Add(new ChunkDraft(
            "body", LineOf(node, body.SpanStart), LineOf(node, body.Span.End - 1), bodyText, 0,
            bodyLines, MaxNestingDepth(body), CyclomaticComplexity(body)));

        if (node.Body is BlockSyntax outer && bodyLines > BlockThresholdLines)
        {
            var ordinal = 1;
            foreach (var candidate in BlockCandidates(outer))
            {
                var lines = LineCount(candidate);
                if (lines < BlockFloorLines || lines >= DominanceShare * bodyLines)
                {
                    continue;
                }

                var text = candidate is BlockSyntax inner
                    ? string.Join(" ", inner.Statements.Select(Normalize))
                    : Normalize(candidate);
                if (text.Length == 0)
                {
                    continue;
                }

                drafts.Add(new ChunkDraft(
                    "block", LineOf(node, candidate.SpanStart), LineOf(node, candidate.Span.End - 1), text, ordinal++, 0, 0, 0));
            }
        }

        return drafts;
    }

    /// <summary>Comment-free, whitespace-collapsed text of a node: the embedding input and the hash input.</summary>
    /// <param name="node">Any syntax node.</param>
    /// <returns>A single-line text; layout and comments cannot change it.</returns>
    public static string Normalize(SyntaxNode node)
    {
        var comments = node.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                             || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                             || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                             || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .ToList();
        var stripped = comments.Count == 0 ? node : node.ReplaceTrivia(comments, (_, _) => default);
        return Whitespace.Replace(stripped.NormalizeWhitespace(indentation: string.Empty, eol: " ").ToFullString(), " ").Trim();
    }

    private static IEnumerable<SyntaxNode> BlockCandidates(BlockSyntax body)
    {
        foreach (var statement in body.Statements)
        {
            switch (statement)
            {
                case TryStatementSyntax tryStatement:
                    yield return tryStatement.Block;
                    foreach (var catchClause in tryStatement.Catches.Where(c => !IsBoilerplateCatch(c)))
                    {
                        yield return catchClause.Block;
                    }

                    if (tryStatement.Finally is not null)
                    {
                        yield return tryStatement.Finally.Block;
                    }

                    break;
                case IfStatementSyntax ifStatement:
                    yield return ifStatement.Statement;
                    var elseClause = ifStatement.Else;
                    while (elseClause is not null)
                    {
                        if (elseClause.Statement is IfStatementSyntax elseIf)
                        {
                            yield return elseIf.Statement;
                            elseClause = elseIf.Else;
                        }
                        else
                        {
                            yield return elseClause.Statement;
                            elseClause = null;
                        }
                    }

                    break;
                case ForEachStatementSyntax loop:
                    yield return loop.Statement;
                    break;
                case ForStatementSyntax loop:
                    yield return loop.Statement;
                    break;
                case WhileStatementSyntax loop:
                    yield return loop.Statement;
                    break;
                case DoStatementSyntax loop:
                    yield return loop.Statement;
                    break;
                case UsingStatementSyntax usingStatement:
                    yield return usingStatement.Statement;
                    break;
                case LockStatementSyntax lockStatement:
                    yield return lockStatement.Statement;
                    break;
                case SwitchStatementSyntax switchStatement:
                    yield return switchStatement;
                    break;
                case LocalFunctionStatementSyntax localFunction when localFunction.Body is not null:
                    yield return localFunction.Body;
                    break;
            }
        }
    }

    private static bool IsBoilerplateCatch(CatchClauseSyntax catchClause)
    {
        var statements = catchClause.Block.Statements;
        if (statements.Count is 0 or > 3)
        {
            return statements.Count == 0;
        }

        return statements.All(statement => statement is ThrowStatementSyntax
            || (statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } }
                && member.Name.Identifier.ValueText.StartsWith("Log", StringComparison.Ordinal)));
    }

    private static int MaxNestingDepth(SyntaxNode body)
    {
        var max = 0;
        foreach (var node in body.DescendantNodes().Where(IsNesting))
        {
            var depth = 1;
            for (var parent = node.Parent; parent is not null && parent != body; parent = parent.Parent)
            {
                if (IsNesting(parent))
                {
                    depth++;
                }
            }

            max = Math.Max(max, depth);
        }

        return max;
    }

    private static bool IsNesting(SyntaxNode node) => node is IfStatementSyntax
        or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax
        or SwitchStatementSyntax or SwitchExpressionSyntax or TryStatementSyntax
        or UsingStatementSyntax or LockStatementSyntax or LocalFunctionStatementSyntax
        or AnonymousFunctionExpressionSyntax;

    private static int CyclomaticComplexity(SyntaxNode body)
    {
        var branches = 0;
        foreach (var node in body.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case IfStatementSyntax:
                case ConditionalExpressionSyntax:
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                case CatchClauseSyntax:
                case CaseSwitchLabelSyntax:
                case CasePatternSwitchLabelSyntax:
                case SwitchExpressionArmSyntax:
                    branches++;
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression)
                                                        || binary.IsKind(SyntaxKind.LogicalOrExpression)
                                                        || binary.IsKind(SyntaxKind.CoalesceExpression):
                    branches++;
                    break;
                case AssignmentExpressionSyntax assignment when assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression):
                    branches++;
                    break;
            }
        }

        return 1 + branches;
    }

    private static int LineCount(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    private static int LineOf(SyntaxNode context, int position)
    {
        var tree = context.SyntaxTree;
        var clamped = Math.Clamp(position, 0, Math.Max(0, tree.Length - 1));
        return tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(clamped, 0)).StartLinePosition.Line + 1;
    }
}
