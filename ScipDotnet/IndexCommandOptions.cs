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
    List<string>? Properties = null
)
{
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