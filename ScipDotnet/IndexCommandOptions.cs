using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

namespace ScipDotnet;

public record IndexCommandOptions(
    FileInfo WorkingDirectory,
    FileInfo Output,
    List<FileInfo> ProjectsFile,
    ILogger<IndexCommandOptions> Logger,
    Matcher Matcher,
    bool AllowGlobalSymbolDefinitions,
    int DotnetRestoreTimeout,
    bool SkipDotnetRestore,
    FileInfo? NugetConfigPath,
    bool UseBuild,
    Uri? OutputUrl = null,
    bool Analyzers = false,
    List<string>? Properties = null,
    bool EmitChunks = true
)
{
    /// <summary>
    /// Vector inputs collected while walking (side-channel next to the SCIP stream —
    /// SCIP models symbols and references, never bodies): per method/constructor a
    /// signature text, a body text with structural facts, and the logical blocks of
    /// long bodies. Content-hashed so the loader embeds only text it has never seen.
    /// </summary>
    public List<ChunkFact> Chunks { get; } = new();

    /// <summary>
    /// Log-template facts collected while walking (side-channel next to the SCIP
    /// stream — the SCIP format itself does not model log templates). Posted to
    /// the output URL with channel=logtemplates after the index upload.
    /// </summary>
    public List<LogTemplateFact> LogTemplates { get; } = new();

    /// <summary>
    /// Precise call edges collected from the semantic model while walking
    /// (side-channel: the SCIP standard has no call graph — a call is an unmarked
    /// reference). Both endpoints are SCIP symbol ids, so the loader links them
    /// with the same MERGE-by-key mechanism as cross-repo references.
    /// </summary>
    public List<CallFact> Calls { get; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether compilation was INCOMPLETE for this
    /// run — the build/restore step failed or the MSBuild workspace reported load
    /// failures — so emitted symbols may be missing and types unresolved. Reported
    /// to the loader so the run's nodes are marked partial.
    /// </summary>
    public bool CompilationIncomplete { get; set; }

    /// <summary>Gets or sets the human-readable reason the run is partial.</summary>
    public string? PartialReason { get; set; }
}

/// <summary>One Log*/BeginScope call site discovered during indexing.</summary>
/// <param name="File">Document path relative to the working directory.</param>
/// <param name="Line">1-based line of the call.</param>
/// <param name="Level">Log level from the method name (Debug/Information/...).</param>
/// <param name="Template">The message template when it is a compile-time constant; null otherwise.</param>
/// <param name="Templated">False when the message is interpolated/built at runtime.</param>
public record LogTemplateFact(string File, int Line, string Level, string? Template, bool Templated);

/// <summary>One precise call edge discovered during indexing.</summary>
/// <param name="CallerSymbol">SCIP symbol id of the enclosing callable (lambdas attributed to their containing member).</param>
/// <param name="CalleeSymbol">SCIP symbol id of the invoked method or constructor.</param>
/// <param name="File">Document path relative to the working directory.</param>
/// <param name="Line">1-based line of the call site.</param>
public record CallFact(string CallerSymbol, string CalleeSymbol, string File, int Line);

/// <summary>One vector input of a method/constructor discovered during indexing.</summary>
/// <param name="Symbol">SCIP symbol id of the owning method/constructor.</param>
/// <param name="Aspect">"signature", "body" or "block".</param>
/// <param name="File">Document path relative to the working directory.</param>
/// <param name="LineStart">1-based first line of the text's source span.</param>
/// <param name="LineEnd">1-based last line of the text's source span.</param>
/// <param name="TextHash">sha256 hex of <paramref name="NormalizedText"/> — the content key.</param>
/// <param name="NormalizedText">Comment-free, whitespace-collapsed text to embed.</param>
/// <param name="Ordinal">Block index within the method; 0 for signature and body.</param>
/// <param name="BodyLines">Body line count (body aspect only).</param>
/// <param name="MaxNestingDepth">Deepest statement nesting (body aspect only).</param>
/// <param name="CyclomaticComplexity">1 + branching nodes (body aspect only).</param>
public record ChunkFact(
    string Symbol,
    string Aspect,
    string File,
    int LineStart,
    int LineEnd,
    string TextHash,
    string NormalizedText,
    int Ordinal,
    int BodyLines,
    int MaxNestingDepth,
    int CyclomaticComplexity);