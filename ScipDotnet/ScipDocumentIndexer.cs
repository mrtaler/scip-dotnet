using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Scip;
using Document = Scip.Document;

namespace ScipDotnet;

/// <summary>
/// Creates SCIP <code>Document</code> based on provided symbols.
/// </summary>
public class ScipDocumentIndexer
{
    /// <summary>
    /// Records a log-template fact for this document (side-channel to the SCIP
    /// stream; the document's relative path is attached here).
    /// </summary>
    /// <param name="line">1-based line of the Log* call.</param>
    /// <param name="level">Log level derived from the method name.</param>
    /// <param name="template">The constant message template, or null.</param>
    /// <param name="templated">Whether the message is a compile-time constant.</param>
    public void RecordLogTemplate(int line, string level, string? template, bool templated) =>
        _options.LogTemplates.Add(new LogTemplateFact(_doc.RelativePath ?? string.Empty, line, level, template, templated));

    /// <summary>
    /// Records a precise call edge (side-channel; rationale kb:e241cdaf): caller and callee
    /// as SCIP symbol ids. Local symbols (lambdas resolved as locals) are skipped —
    /// their ids are document-scoped counters, meaningless as graph keys.
    /// </summary>
    /// <param name="caller">The enclosing callable symbol at the call site.</param>
    /// <param name="callee">The invoked method or constructor symbol.</param>
    /// <param name="line">1-based line of the call site.</param>
    public void RecordCall(ISymbol caller, ISymbol callee, int line)
    {
        var callerScip = CreateScipSymbol(caller);
        var calleeScip = CreateScipSymbol(callee);
        if (callerScip.IsLocal() || calleeScip.IsLocal()
            || callerScip == ScipSymbol.Empty || calleeScip == ScipSymbol.Empty)
        {
            return;
        }

        _options.Calls.Add(new CallFact(
            callerScip.Value, calleeScip.Value, _doc.RelativePath ?? string.Empty, line));
    }

    /// <summary>
    /// Records the vector inputs of a method/constructor (side-channel next to the SCIP
    /// stream). Skipped when chunk collection is disabled, for generated documents (protobuf
    /// and gRPC stubs, source-generator output — their bodies are templates repeated per
    /// message and would dominate every similarity search), and for local or unresolved
    /// symbols — their SCIP ids are document-scoped counters, meaningless as graph keys.
    /// </summary>
    /// <param name="symbol">The declared method or constructor symbol.</param>
    /// <param name="drafts">The drafts produced by <see cref="MethodChunker"/>.</param>
    public void RecordChunks(ISymbol? symbol, IReadOnlyList<ChunkDraft> drafts)
    {
        if (!_options.EmitChunks || symbol is null || drafts.Count == 0
            || (DocumentRoles & (int)SymbolRole.Generated) != 0)
        {
            return;
        }

        var scip = CreateScipSymbol(symbol);
        if (scip.IsLocal() || scip == ScipSymbol.Empty)
        {
            return;
        }

        foreach (var draft in drafts)
        {
            _options.Chunks.Add(new ChunkFact(
                scip.Value, draft.Aspect, _doc.RelativePath ?? string.Empty, draft.LineStart, draft.LineEnd,
                draft.TextHash, draft.NormalizedText, draft.Ordinal, draft.BodyLines, draft.BodyTokens,
                draft.BodyStatements, draft.MaxNestingDepth, draft.CyclomaticComplexity));
        }
    }

    /// <summary>
    /// Extra SCIP roles OR-ed into every occurrence of this document: Test for
    /// test-project documents, Generated for source-generated documents.
    /// </summary>
    public int DocumentRoles { get; set; }

    private readonly Document _doc;
    private readonly IndexCommandOptions _options;
    private int _localCounter;
    private readonly Dictionary<ISymbol, ScipSymbol> _globals;
    private readonly Dictionary<ISymbol, ScipSymbol> _locals = new(SymbolEqualityComparer.Default);
    private readonly string _markdownCodeFenceLanguage;

    // Custom formatting options to render symbol documentation. Feel free to tweak these parameters.
    // The options were derived by multiple rounds of experimentation with the goal of striking a
    // balance between showing detailed/accurate information without using too verbose syntax.
    private readonly SymbolDisplayFormat _format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.OmittedAsContaining,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters |
                         SymbolDisplayGenericsOptions.IncludeVariance |
                         SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility |
                       SymbolDisplayMemberOptions.IncludeModifiers |
                       SymbolDisplayMemberOptions.IncludeParameters |
                       SymbolDisplayMemberOptions.IncludeRef |
                       SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeConstantValue |
                       SymbolDisplayMemberOptions.IncludeContainingType |
                       SymbolDisplayMemberOptions.IncludeExplicitInterface,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        extensionMethodStyle: SymbolDisplayExtensionMethodStyle.InstanceMethod,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                          SymbolDisplayParameterOptions.IncludeName |
                          SymbolDisplayParameterOptions.IncludeDefaultValue |
                          SymbolDisplayParameterOptions.IncludeExtensionThis |
                          SymbolDisplayParameterOptions.IncludeOptionalBrackets |
                          SymbolDisplayParameterOptions.IncludeParamsRefOut,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        localOptions: SymbolDisplayLocalOptions.IncludeType |
                      SymbolDisplayLocalOptions.IncludeRef |
                      SymbolDisplayLocalOptions.IncludeConstantValue,
        kindOptions: SymbolDisplayKindOptions.IncludeTypeKeyword |
                     SymbolDisplayKindOptions.IncludeMemberKeyword |
                     SymbolDisplayKindOptions.IncludeNamespaceKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.AllowDefaultLiteral |
                              SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                              SymbolDisplayMiscellaneousOptions.UseAsterisksInMultiDimensionalArrays |
                              SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                              SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
    );

    public ScipDocumentIndexer(
        Document doc,
        IndexCommandOptions options,
        Dictionary<ISymbol, ScipSymbol> globals)
    {
        _doc = doc;
        _options = options;
        _globals = globals;
        _markdownCodeFenceLanguage = _doc.Language == "C#" ? "cs" : "vb";
    }

    /// <summary>
    /// Builds the SCIP symbol id for <paramref name="sym"/> by chaining descriptors
    /// through its containing symbols. Namespaces chain through their containing
    /// namespace and only the OUTERMOST namespace anchors the package: upstream
    /// anchored EVERY namespace directly to the package, truncating the descriptor
    /// chain to the innermost segment, so A.B.X.Foo and A.C.X.Foo collided on the
    /// same "X/Foo#" id and distinct classes merged into one graph node.
    /// </summary>
    /// <param name="sym">The Roslyn symbol to encode, or null for an empty symbol.</param>
    /// <returns>The SCIP symbol id.</returns>
    private ScipSymbol CreateScipSymbol(ISymbol? sym)
    {
        if (sym == null)
        {
            return ScipSymbol.Empty;
        }

        var fromCache = _globals.GetValueOrDefault(sym, ScipSymbol.Empty);
        if (fromCache != ScipSymbol.Empty)
        {
            return fromCache;
        }

        if (IsLocalSymbol(sym))
        {
            return CreateLocalScipSymbol(sym);
        }

        var owner = sym switch
        {
            INamespaceSymbol { ContainingNamespace: { IsGlobalNamespace: false } containingNamespace } =>
                CreateScipSymbol(containingNamespace),
            INamespaceSymbol => CreateScipPackageSymbol(sym),
            _ => CreateScipSymbol(sym.ContainingSymbol),
        };

        if (owner.IsLocal())
        {
            return CreateLocalScipSymbol(sym);
        }

        var result = ScipSymbol.Global(owner, new SymbolDescriptor
        {
            Name = sym.Name,
            Suffix = SymbolSuffix(sym),
            Disambiguator = MethodDisambiguator(sym)
        });
        _globals.TryAdd(sym, result);
        return result;
    }

    private ScipSymbol CreateLocalScipSymbol(ISymbol sym)
    {
        var local = _locals.GetValueOrDefault(sym, ScipSymbol.Empty);
        if (local != ScipSymbol.Empty)
        {
            return local;
        }

        var localResult = ScipSymbol.Local(_localCounter++);
        _locals.TryAdd(sym, localResult);
        return localResult;
    }

    private ScipSymbol CreateScipPackageSymbol(ISymbol sym)
    {
        if (sym.ContainingAssembly == null)
        {
            return ScipSymbol.IndexLocalPackage;
        }

        if (!_options.AllowGlobalSymbolDefinitions && sym.Locations.Any(location => location.IsInSource))
        {
            // Emit index-local symbols to avoid exporting public symbols into the global scope (all repos in the world).
            // We have no guarantee that a random csproj file from any random repository is publishing to NuGet.
            // Use the command-line flag --allow-global-symbol-definitions to disable this behavior.
            return ScipSymbol.IndexLocalPackage;
        }

        return ScipSymbol.Package(
            sym.ContainingAssembly.Identity.Name,
            sym.ContainingAssembly.Identity.Version.ToString());
    }

    private SymbolDescriptor.Types.Suffix SymbolSuffix(ISymbol sym)
    {
        switch (sym.Kind)
        {
            case SymbolKind.Namespace:
                return SymbolDescriptor.Types.Suffix.Package;
            case SymbolKind.NamedType:
            case SymbolKind.FunctionPointerType:
            case SymbolKind.ErrorType:
            case SymbolKind.PointerType:
            case SymbolKind.ArrayType:
            case SymbolKind.DynamicType:
            case SymbolKind.Alias:
            case SymbolKind.Event:
                return SymbolDescriptor.Types.Suffix.Type;
            case SymbolKind.Property:
            case SymbolKind.Field:
            case SymbolKind.Assembly:
            case SymbolKind.Label:
            case SymbolKind.NetModule:
            case SymbolKind.RangeVariable:
            case SymbolKind.Preprocessing:
            case SymbolKind.Discard:
                return SymbolDescriptor.Types.Suffix.Term;
            case SymbolKind.Method:
                return SymbolDescriptor.Types.Suffix.Method;
            case SymbolKind.Parameter:
                return SymbolDescriptor.Types.Suffix.Parameter;
            case SymbolKind.TypeParameter:
                return SymbolDescriptor.Types.Suffix.TypeParameter;
            case SymbolKind.Local:
                return SymbolDescriptor.Types.Suffix.Local;
            default:
                _options.Logger.LogWarning("unknown symbol kind {SymKind}", sym.Kind);
                return SymbolDescriptor.Types.Suffix.Meta;
        }
    }

    private static string MethodDisambiguator(ISymbol sym)
    {
        if (sym is not IMethodSymbol)
        {
            return "";
        }

        var overloadCount = 0;
        foreach (var member in sym.ContainingType.GetMembers())
        {
            if (member.Equals(sym, SymbolEqualityComparer.Default))
            {
                return overloadCount == 0 ? "" : $"+{overloadCount}";
            }

            if (member.Name.Equals(sym.Name))
            {
                overloadCount++;
            }
        }

        return "";
    }

    private readonly string[] _isIgnoredRelationshipSymbol =
    {
        " System/Object#",
        " System/Enum#",
        " System/ValueType#",
    };

    // Returns true if this symbol should not be emitted as a SymbolInformation relationship symbol.
    // The reason we ignore these symbols is because they appear automatically for a large number of
    // symbols putting pressure on our backend to index the inverted index. It's not particularly useful anyways
    // to query all the implementations of something like System/Object#.

    /// <summary>
    /// Maps a Roslyn symbol to the exact SCIP SymbolInformation.Kind, replacing the
    /// loader-side guessing from descriptor suffixes (which could not tell an
    /// interface from a class without parsing the signature text).
    /// </summary>
    private static SymbolInformation.Types.Kind MapKind(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol { TypeKind: TypeKind.Interface } => SymbolInformation.Types.Kind.Interface,
            INamedTypeSymbol { TypeKind: TypeKind.Enum } => SymbolInformation.Types.Kind.Enum,
            INamedTypeSymbol { TypeKind: TypeKind.Struct } => SymbolInformation.Types.Kind.Struct,
            INamedTypeSymbol { TypeKind: TypeKind.Delegate } => SymbolInformation.Types.Kind.Delegate,
            INamedTypeSymbol { IsRecord: true } => SymbolInformation.Types.Kind.Class,
            INamedTypeSymbol => SymbolInformation.Types.Kind.Class,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => SymbolInformation.Types.Kind.Constructor,
            IMethodSymbol { IsExtensionMethod: true } => SymbolInformation.Types.Kind.Method,
            IMethodSymbol => SymbolInformation.Types.Kind.Method,
            IPropertySymbol => SymbolInformation.Types.Kind.Property,
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => SymbolInformation.Types.Kind.EnumMember,
            IFieldSymbol => SymbolInformation.Types.Kind.Field,
            IEventSymbol => SymbolInformation.Types.Kind.Event,
            IParameterSymbol => SymbolInformation.Types.Kind.Parameter,
            ITypeParameterSymbol => SymbolInformation.Types.Kind.TypeParameter,
            INamespaceSymbol => SymbolInformation.Types.Kind.Namespace,
            _ => SymbolInformation.Types.Kind.UnspecifiedKind,
        };

    private bool IsIgnoredRelationshipSymbol(string symbol) =>
        _isIgnoredRelationshipSymbol.Any(symbol.EndsWith);

    public void VisitOccurrence(ISymbol? symbol, Location location, bool isDefinition,
        Location? enclosingLocation = null, AccessKind accessKind = AccessKind.Read)
    {
        if (symbol == null)
        {
            return;
        }

        var symbolRole = 0;
        if (isDefinition)
        {
            symbolRole |= (int)SymbolRole.Definition;
        }
        else
        {
            if (accessKind is AccessKind.Write or AccessKind.ReadWrite)
            {
                symbolRole |= (int)SymbolRole.WriteAccess;
            }

            if (accessKind is AccessKind.Read or AccessKind.ReadWrite)
            {
                symbolRole |= (int)SymbolRole.ReadAccess;
            }
        }

        if (DocumentRoles != 0)
        {
            symbolRole |= DocumentRoles;
        }

        var scipSymbol = CreateScipSymbol(symbol).Value;
        var occurrence = new Occurrence
        {
            Symbol = scipSymbol,
            SymbolRoles = symbolRole
        };
        _doc.Occurrences.Add(occurrence);
        foreach (var range in LocationToRange(location))
        {
            occurrence.Range.Add(range);
        }

        if (enclosingLocation != null)
        {
            foreach (var range in LocationToRange(enclosingLocation))
            {
                occurrence.EnclosingRange.Add(range);
            }
        }

        if (!isDefinition) return;

        // Emit SymbolInformation for this definition occurrence.
        var info = new SymbolInformation { Symbol = scipSymbol, Kind = MapKind(symbol) };
        _doc.Symbols.Add(info);

        if (symbol is IMethodSymbol { OverriddenMethod: not null } overridingMethod)
        {
            var overriddenScip = CreateScipSymbol(overridingMethod.OverriddenMethod).Value;
            if (!IsIgnoredRelationshipSymbol(overriddenScip))
            {
                info.Relationships.Add(new Relationship
                {
                    Symbol = overriddenScip,
                    IsReference = true,
                    IsImplementation = true,
                });
            }
        }

        var symbolSignature = symbol.ToDisplayString(_format);
        if (symbolSignature.Length > 0)
        {
            info.Documentation.Add($"```{_markdownCodeFenceLanguage}\n{symbolSignature}\n```");
        }

        var symbolDocumentation = symbol.GetDocumentationCommentXml();
        if (symbolDocumentation?.Length > 0)
        {
            info.Documentation.Add(symbolDocumentation);
        }

        switch (symbol)
        {
            case INamedTypeSymbol namedTypeSymbol:
                {
                    var baseType = namedTypeSymbol.BaseType;
                    while (baseType != null)
                    {
                        var baseTypeSymbol = CreateScipSymbol(baseType).Value;
                        if (IsIgnoredRelationshipSymbol(baseTypeSymbol))
                        {
                            break;
                        }

                        info.Relationships.Add(new Relationship
                        {
                            Symbol = baseTypeSymbol,
                            IsImplementation = true
                        });
                        baseType = baseType.BaseType;
                    }

                    foreach (var interfaceSymbol in namedTypeSymbol.AllInterfaces)
                    {
                        var interfaceSymbolSymbol = CreateScipSymbol(interfaceSymbol).Value;
                        if (IsIgnoredRelationshipSymbol(interfaceSymbolSymbol))
                        {
                            continue;
                        }

                        info.Relationships.Add(new Relationship
                        {
                            Symbol = interfaceSymbolSymbol,
                            IsImplementation = true
                        });
                    }

                    break;
                }
            case IMethodSymbol methodSymbol:
                {
                    var overriddenMethod = methodSymbol.OverriddenMethod;
                    while (overriddenMethod != null)
                    {
                        info.Relationships.Add(new Relationship
                        {
                            Symbol = CreateScipSymbol(overriddenMethod).Value,
                            IsImplementation = true,
                            IsReference = true
                        });
                        overriddenMethod = overriddenMethod.OverriddenMethod;
                    }

                    foreach (var interfaceMethod in ScipDocumentIndexer.InterfaceImplementations(methodSymbol))
                    {
                        info.Relationships.Add(new Relationship
                        {
                            Symbol = CreateScipSymbol(interfaceMethod).Value,
                            IsImplementation = true,
                            IsReference = true
                        });
                    }

                    break;
                }
        }
    }

    // Returns explicitly and implicitly implemented interface methods by the given symbol method.
    // The Roslyn API has a `ExplicitInterfaceImplementations` that does not return implicitly implemented
    // methods.
    private static IEnumerable<ISymbol> InterfaceImplementations(IMethodSymbol symbol)
    {
        foreach (var interfaceSymbol in symbol.ContainingType.AllInterfaces)
        {
            foreach (var interfaceMember in interfaceSymbol.GetMembers())
            {
                var implementation = symbol.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation != null && symbol.Equals(implementation, SymbolEqualityComparer.Default))
                {
                    yield return interfaceMember;
                }
            }
        }
    }

    // Converts a Roslyn location into a SCIP range.
    private static IEnumerable<int> LocationToRange(Location location)
    {
        var span = location.GetMappedLineSpan();
        if (span.StartLinePosition.Line == span.EndLinePosition.Line)
        {
            return new[]
                {
                    span.StartLinePosition.Line,
                    span.StartLinePosition.Character,
                    span.EndLinePosition.Character
                };
        }

        return new[]
            {
                span.StartLinePosition.Line,
                span.StartLinePosition.Character,
                span.EndLinePosition.Line,
                span.EndLinePosition.Character
            };
    }

    private static bool IsLocalSymbol(ISymbol sym)
    {
        return sym.Kind == SymbolKind.Local ||
               sym.Kind == SymbolKind.RangeVariable ||
               sym.Kind == SymbolKind.TypeParameter ||
               sym is IMethodSymbol { MethodKind: MethodKind.LocalFunction } ||
               // Anonymous classes/methods have empty names and can not be accessed outside their file.
               // The "global namespace" (parent of all namespaces) also has an empty name and should not
               // be treated as a local variable.
               (sym.Name.Equals("") && sym.Kind != SymbolKind.Namespace);
    }
}

/// <summary>How an occurrence accesses its symbol, mapped to SCIP WriteAccess/ReadAccess roles.</summary>
public enum AccessKind
{
    /// <summary>The symbol is only read.</summary>
    Read,

    /// <summary>The symbol is only written (plain assignment target, out argument).</summary>
    Write,

    /// <summary>The symbol is read and written (compound assignment, ++/--, ref argument).</summary>
    ReadWrite,
}
