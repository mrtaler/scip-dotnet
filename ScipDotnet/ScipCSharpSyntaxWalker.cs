using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ScipDotnet;

/// <summary>
/// Walks a single C# syntax tree and produces a SCIP <code>Document</code>.
/// </summary>
public class ScipCSharpSyntaxWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;
    private readonly ScipDocumentIndexer _scipDocumentIndexer;

    public ScipCSharpSyntaxWalker(ScipDocumentIndexer scipSymbolFormatter, SemanticModel semanticModel, SyntaxWalkerDepth depth = SyntaxWalkerDepth.Node) : base(depth)
    {
        _scipDocumentIndexer = scipSymbolFormatter;
        _semanticModel = semanticModel;
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        if (!node.IsVar)
        {
            _scipDocumentIndexer.VisitOccurrence(
                _semanticModel.GetSymbolInfo(node).Symbol,
                node.GetLocation(),
                false,
                accessKind: ClassifyAccess(node));
        }

        base.VisitIdentifierName(node);
    }

    /// <summary>
    /// Classifies whether an identifier usage READS or WRITES the symbol, walking up
    /// through member-access wrappers to the owning expression: left side of an
    /// assignment (including compound += and deconstruction), ++/--, and ref/out
    /// arguments are writes; compound assignment and ++/-- also read.
    /// </summary>
    private static AccessKind ClassifyAccess(SyntaxNode node)
    {
        var current = node;
        while (current.Parent is MemberAccessExpressionSyntax member && member.Name == current)
        {
            current = member;
        }

        var parent = current.Parent;
        return parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Left == current =>
                assignment.Kind() == SyntaxKind.SimpleAssignmentExpression
                    ? AccessKind.Write
                    : AccessKind.ReadWrite,
            PrefixUnaryExpressionSyntax prefix when
                prefix.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression =>
                AccessKind.ReadWrite,
            PostfixUnaryExpressionSyntax postfix when
                postfix.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression =>
                AccessKind.ReadWrite,
            ArgumentSyntax argument when !argument.RefOrOutKeyword.IsKind(SyntaxKind.None) =>
                argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword)
                    ? AccessKind.Write
                    : AccessKind.ReadWrite,
            _ => AccessKind.Read,
        };
    }

    private static readonly HashSet<string> LogMethodNames = new(StringComparer.Ordinal)
    {
        "LogTrace", "LogDebug", "LogInformation", "LogWarning", "LogError", "LogCritical", "Log", "BeginScope",
    };

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        CollectLogTemplate(node);
        CollectCall(node);
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        CollectCall(node);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        CollectCall(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        CollectCall(node);
        base.VisitConstructorInitializer(node);
    }

    /// <summary>
    /// Records a precise call edge from the semantic model (rationale kb:e241cdaf): the
    /// invoked method/constructor plus the enclosing callable at the call site.
    /// Calls inside lambdas and local functions are attributed to the containing
    /// MEMBER (GetEnclosingSymbol returns the lambda symbol; walking up
    /// ContainingSymbol reaches the method) — matching how a stack trace reads.
    /// </summary>
    /// <param name="node">The invocation-shaped syntax node.</param>
    private void CollectCall(SyntaxNode node)
    {
        if (_semanticModel.GetSymbolInfo(node).Symbol is not IMethodSymbol callee)
        {
            return;
        }

        var caller = _semanticModel.GetEnclosingSymbol(node.SpanStart);
        while (caller is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
        {
            caller = caller.ContainingSymbol;
        }

        if (caller == null)
        {
            return;
        }

        _scipDocumentIndexer.RecordCall(
            caller,
            callee,
            node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    }

    /// <summary>
    /// Records a LogTemplateFact for ILogger call sites (side-channel; rationale kb:e241cdaf):
    /// the message template when it is a compile-time constant (literal or constant
    /// concatenation — resolved via GetConstantValue), or templated=false when the
    /// message is interpolated/built at runtime — those sites break structured
    /// logging and cannot be matched back from aggregated log text.
    /// </summary>
    private void CollectLogTemplate(InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax member
            || !LogMethodNames.Contains(member.Name.Identifier.ValueText))
        {
            return;
        }

        if (_semanticModel.GetSymbolInfo(node).Symbol is not IMethodSymbol method
            || method.ContainingNamespace?.ToDisplayString().StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) != true)
        {
            return;
        }

        var messageArgument = node.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .FirstOrDefault(expression =>
                _semanticModel.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_String);
        if (messageArgument == null)
        {
            return;
        }

        var constant = _semanticModel.GetConstantValue(messageArgument);
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var level = member.Name.Identifier.ValueText.StartsWith("Log", StringComparison.Ordinal)
            ? member.Name.Identifier.ValueText["Log".Length..]
            : member.Name.Identifier.ValueText;
        _scipDocumentIndexer.RecordLogTemplate(
            line,
            string.IsNullOrEmpty(level) ? "Unspecified" : level,
            constant.HasValue ? constant.Value as string : null,
            constant.HasValue);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        // A generic invocation like AddFoo<T>(...) is a GenericNameSyntax, NOT an
        // IdentifierNameSyntax - without this override such calls produced no
        // reference occurrence at all ("who uses X" missed every generic call).
        // The occurrence anchors on the identifier only, excluding the <T> part.
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetSymbolInfo(node).Symbol, node.Identifier.GetLocation(), false);
        base.VisitGenericName(node);
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitClassDeclaration(node);
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitRecordDeclaration(node);
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitEnumDeclaration(node);
    }

    public override void VisitCatchDeclaration(CatchDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitCatchDeclaration(node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        _scipDocumentIndexer.VisitOccurrence(symbol, node.Identifier.GetLocation(), true,
            node.GetLocation());
        _scipDocumentIndexer.RecordChunks(symbol, MethodChunker.Collect(node));
        base.VisitConstructorDeclaration(node);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitDelegateDeclaration(node);
    }

    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitDestructorDeclaration(node);
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitEventDeclaration(node);
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitInterfaceDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitStructDeclaration(node);
    }

    public override void VisitVariableDeclarator(VariableDeclaratorSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitVariableDeclarator(node);
    }

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitEnumMemberDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        _scipDocumentIndexer.VisitOccurrence(symbol, node.Identifier.GetLocation(), true,
            node.GetLocation());
        _scipDocumentIndexer.RecordChunks(symbol, MethodChunker.Collect(node));
        base.VisitMethodDeclaration(node);
    }

    public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true,
            node.GetLocation());
        base.VisitLocalFunctionStatement(node);
    }

    public override void VisitParameter(ParameterSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitParameter(node);
    }

    public override void VisitSingleVariableDesignation(SingleVariableDesignationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitSingleVariableDesignation(node);
    }

    public override void VisitTypeParameter(TypeParameterSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitTypeParameter(node);
    }

    public override void VisitForEachStatement(ForEachStatementSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitForEachStatement(node);
    }

    public override void VisitFromClause(FromClauseSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitFromClause(node);
    }

    public override void VisitJoinClause(JoinClauseSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitJoinClause(node);
    }

    public override void VisitJoinIntoClause(JoinIntoClauseSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitJoinIntoClause(node);
    }

    public override void VisitLetClause(LetClauseSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitLetClause(node);
    }

    public override void VisitQueryContinuation(QueryContinuationSyntax node)
    {
        _scipDocumentIndexer.VisitOccurrence(_semanticModel.GetDeclaredSymbol(node), node.Identifier.GetLocation(), true);
        base.VisitQueryContinuation(node);
    }
}