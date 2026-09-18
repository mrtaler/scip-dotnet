using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScipDotnet;

/// <summary>
/// Orchestrates Roslyn and MSBuild APIs to SCIP index a given project.
/// </summary>
public class ScipProjectIndexer
{
    public ScipProjectIndexer(ILogger<ScipProjectIndexer> logger) =>
        Logger = logger;

    private ILogger<ScipProjectIndexer> Logger { get; }

    /// <summary>
    /// Restores (or, with UseBuild, builds) the target before indexing. The target
    /// path is passed EXPLICITLY: a bare "dotnet build" in the repo root discovers the
    /// SOLUTION and builds everything, so a per-project run of one cross-platform csproj
    /// was dragged down by a Windows-only project elsewhere in the .sln.
    /// /nodeReuse:false — never attach to foreign MSBuild nodes: reusing an IDE-owned
    /// node (same SDK, different environment) hangs the handshake forever (observed with
    /// Rider's nodes on macOS under launchd). With UseBuild the sources are BUILT (not
    /// just restored) so source generators materialize their output
    /// (EmitCompilerGeneratedFiles into obj/generated) — making generated code indexable.
    /// </summary>
    /// <param name="options">The index command options (verb, properties, nuget config).</param>
    /// <param name="project">The solution or project to restore/build.</param>
    private void Restore(IndexCommandOptions options, FileInfo project)
    {
        var verb = options.UseBuild ? "build" : "restore";
        var extraProps = options.UseBuild ? " /p:EmitCompilerGeneratedFiles=true" : "";
        foreach (var pair in options.Properties ?? new List<string>())
        {
            extraProps += $" \"/p:{pair}\"";
        }

        var arguments = $"{verb} \"{project.FullName}\" /p:EnableWindowsTargeting=true /nodeReuse:false{extraProps}";
        if (options.NugetConfigPath != null)
        {
            arguments += $" --configfile \"{options.NugetConfigPath.FullName}\"";
        }
        var process = new Process()
        {
            StartInfo = new ProcessStartInfo()
            {
                WorkingDirectory = options.WorkingDirectory.FullName,
                FileName = "dotnet",
                Arguments = arguments
            }
        };
        options.Logger.LogInformation("$ dotnet {Arguments}", arguments);
        process.Start();
        if (!process.WaitForExit(options.DotnetRestoreTimeout))
        {
            Logger.LogWarning("Dotnet restore did not finish in {Time} milliseconds, the results of the indexing might be incorrect.", options.DotnetRestoreTimeout);
            options.CompilationIncomplete = true;
            options.PartialReason = $"{verb} did not finish within {options.DotnetRestoreTimeout} ms";
            return;
        }

        if (process.ExitCode != 0)
        {
            options.CompilationIncomplete = true;
            options.PartialReason = $"{verb} exited {process.ExitCode} (types may be unresolved)";
            Logger.LogWarning("{Verb} exited {Code} for {Project} — symbols may be incomplete",
                verb, process.ExitCode, project.Name);
        }
    }

    public async IAsyncEnumerable<Scip.Document> IndexDocuments(IHost host, IndexCommandOptions options)
    {
        var indexedProjects = new HashSet<ProjectId>();
        foreach (var project in options.ProjectsFile)
        {
            await foreach (var document in IndexProject(host, options, project, indexedProjects))
            {
                yield return document;
            }
        }
    }

    private async IAsyncEnumerable<Scip.Document> IndexProject(IHost host,
                                                               IndexCommandOptions options,
                                                               FileInfo rootProject,
                                                               HashSet<ProjectId> indexedProjects)
    {
        if (!options.SkipDotnetRestore)
        {
            Restore(options, rootProject);
        }

        var isProjectFile = string.Equals(rootProject.Extension, ".csproj", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(rootProject.Extension, ".vbproj", StringComparison.OrdinalIgnoreCase);
        var projects = (isProjectFile
            ? new[]
            {
                await host.Services.GetRequiredService<MSBuildWorkspace>()
                    .OpenProjectAsync(rootProject.FullName)
            }
            : (await host.Services.GetRequiredService<MSBuildWorkspace>()
                .OpenSolutionAsync(rootProject.FullName)).Projects).ToList();


        options.Logger.LogDebug($"Found {projects.Count()} projects");
        var projectsPerProjFile = projects.GroupBy(x => x.FilePath);
        var framework = $"net{Environment.Version.Major}.0";
        foreach (var projectGroup in projectsPerProjFile)
        {

            // If the project was found by opening the solution, we need to find the project that matches the framework.
            // if we can' fall back to the first one. Without this, we will process the same document multiple times
            // once for each framework version being targeting and it leads to unpredictable results since the scip file
            // will contain the same document multiple times iwth different symbols.
            var project = projectGroup.FirstOrDefault(x => x.Name.Contains($"({framework})", StringComparison.OrdinalIgnoreCase)) ?? projectGroup.First();
            if (project.Language != "C#" && project.Language != "Visual Basic")
            {
                Logger.LogWarning(
                    "Skipping project {ProjectFilePath} because it has language {ProjectLanguage} and scip-dotnet currently only supports C# and Visual Basic.",
                    project.FilePath, project.Language);
                continue;
            }

            if (indexedProjects.Contains(project.Id))
            {
                continue;
            }

            indexedProjects.Add(project.Id);

            var globals = new Dictionary<ISymbol, ScipSymbol>(SymbolEqualityComparer.Default);
            var analyzerDiagnostics = options.Analyzers
                ? await GetAnalyzerDiagnosticsAsync(project, options)
                : null;

            options.Logger.LogDebug($"Found {project.Documents.Count()} documents in {projectGroup.Key}");
            foreach (var document in project.Documents)
            {
                if (options.Matcher.Match(options.WorkingDirectory.FullName, document.FilePath).HasMatches)
                {
                    yield return await IndexDocument(
                        document, options, globals, project.Language,
                        analyzerDiagnostics: analyzerDiagnostics?[document.FilePath ?? string.Empty]);
                }
                else
                {
                    options.Logger.LogDebug(
                        "Excluded file path '{FilePath}' because it did not match the provided --include and --exclude arguments",
                        document.FilePath);
                }
            }

            if (options.UseBuild)
            {
                // Source-generated documents (API clients, protobuf, etc.) exist only
                // inside the compilation - they are not in project.Documents. Index them
                // under a synthetic [generated]/ path so their definitions land in the
                // graph like any other code.
                var generatedDocuments = await project.GetSourceGeneratedDocumentsAsync();
                var generatedCount = 0;
                foreach (var generated in generatedDocuments)
                {
                    var scipDocument = await IndexDocument(
                        generated, options, globals, project.Language,
                        $"[generated]/{project.Name}/{generated.HintName}");
                    generatedCount++;
                    yield return scipDocument;
                }

                if (generatedCount > 0)
                {
                    options.Logger.LogInformation(
                        "Indexed {Count} source-generated documents in {Project}",
                        generatedCount, project.Name);
                }
            }
        }
    }

    /// <summary>
    /// Runs the project's configured Roslyn analyzers (the same set a build would run:
    /// StyleCop/Sonar/etc. referenced by the project) and groups their diagnostics by
    /// document file path. Returns null when the compilation is unavailable or the
    /// project has no analyzers.
    /// </summary>
    /// <param name="project">The Roslyn project to analyze.</param>
    /// <param name="options">The index command options.</param>
    /// <returns>Diagnostics grouped by document file path, or null.</returns>
    private async Task<ILookup<string, Diagnostic>?> GetAnalyzerDiagnosticsAsync(
        Project project,
        IndexCommandOptions options)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation == null)
        {
            return null;
        }

        var analyzers = project.AnalyzerReferences
            .SelectMany(reference => reference.GetAnalyzers(project.Language))
            .ToImmutableArray();
        if (analyzers.IsEmpty)
        {
            options.Logger.LogInformation("No analyzers configured for {Project}", project.Name);
            return null;
        }

        var stopwatch = Stopwatch.StartNew();
        var withAnalyzers = compilation.WithAnalyzers(
            analyzers,
            new CompilationWithAnalyzersOptions(
                project.AnalyzerOptions,
                onAnalyzerException: null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        options.Logger.LogInformation(
            "Analyzers: {Count} diagnostics from {Analyzers} analyzers in {Project} ({ElapsedMs} ms)",
            diagnostics.Length, analyzers.Length, project.Name, stopwatch.ElapsedMilliseconds);
        return diagnostics
            .Where(diagnostic => diagnostic.Location.SourceTree != null)
            .ToLookup(diagnostic => diagnostic.Location.SourceTree!.FilePath, StringComparer.Ordinal);
    }

    /// <summary>
    /// Detects document-wide SCIP roles: Test for documents of test projects
    /// (project or assembly name containing "Test", or a test/ path segment),
    /// Generated for generated documents — SourceGeneratedDocument, anything under an
    /// obj/ directory (source generators with EmitCompilerGeneratedFiles, Grpc.Tools and
    /// protobuf stubs, Razor output), and the conventional *.g.cs / *.designer.cs /
    /// *.generated.cs suffixes. Generated members stay indexable as symbols; consumers
    /// that reason about hand-written logic (vector inputs, duplicate detection) skip them.
    /// </summary>
    private static int DetectDocumentRoles(Document document)
    {
        var roles = 0;
        var projectName = document.Project.Name;
        var filePath = document.FilePath ?? string.Empty;
        var isTest = projectName.Contains("Test", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains($"{Path.DirectorySeparatorChar}test{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || filePath.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        if (isTest)
        {
            roles |= (int)Scip.SymbolRole.Test;
        }

        var isGenerated = document is Microsoft.CodeAnalysis.SourceGeneratedDocument
            || filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || filePath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
        if (isGenerated)
        {
            roles |= (int)Scip.SymbolRole.Generated;
        }

        return roles;
    }

    /// <summary>
    /// Indexes a single document into a SCIP document. The synthetic
    /// <paramref name="relativePathOverride"/> for source-generated documents must be
    /// applied BEFORE walking: LogTemplateFacts capture the document path at record
    /// time, and rewriting the path afterwards left them pointing at obj/... paths
    /// where no symbols live.
    /// </summary>
    /// <param name="document">The Roslyn document to index.</param>
    /// <param name="options">The index command options.</param>
    /// <param name="globals">The cross-document symbol cache of the project.</param>
    /// <param name="language">The project language (C# or Visual Basic).</param>
    /// <param name="relativePathOverride">Synthetic path for source-generated documents, or null to derive from the file path.</param>
    /// <param name="analyzerDiagnostics">Analyzer diagnostics of this document, or null when analyzers did not run.</param>
    /// <returns>The indexed SCIP document.</returns>
    private async Task<Scip.Document> IndexDocument(Document document,
                                                    IndexCommandOptions options,
                                                    Dictionary<ISymbol, ScipSymbol> globals,
                                                    string language,
                                                    string? relativePathOverride = null,
                                                    IEnumerable<Diagnostic>? analyzerDiagnostics = null)
    {
        Scip.Document doc = new()
        {
            Language = language,
            PositionEncoding = Scip.PositionEncoding.Utf16CodeUnitOffsetFromLineStart,
            RelativePath = relativePathOverride ?? (document.FilePath == null
                ? null
                : Path.GetRelativePath(options.WorkingDirectory.FullName, document.FilePath))
        };
        var semanticModel = await document.GetSemanticModelAsync();
        if (semanticModel == null)
        {
            Logger.LogWarning(
                "Skipping document {DocumentFilePath} because document.GetSemanticModelAsync() returned null",
                document.FilePath);
        }
        else
        {
            var symbolFormatter = new ScipDocumentIndexer(doc, options, globals)
            {
                DocumentRoles = DetectDocumentRoles(document),
            };
            var root = await document.GetSyntaxRootAsync();
            if (language == "C#")
            {
                var walker = new ScipCSharpSyntaxWalker(symbolFormatter, semanticModel);
                walker.Visit(root);
            }
            else if (language == "Visual Basic")
            {
                var walker = new ScipVisualBasicSyntaxWalker(symbolFormatter, semanticModel);
                walker.Visit(root);
            }

            EmitDiagnostics(doc, semanticModel.GetDiagnostics(), "compiler");
            if (analyzerDiagnostics != null)
            {
                EmitDiagnostics(doc, analyzerDiagnostics, "analyzer");
            }
        }

        return doc;
    }

    /// <summary>
    /// Emits Roslyn diagnostics into the SCIP document as symbol-less occurrences
    /// carrying Occurrence.Diagnostics (the standard field scip-dotnet upstream never
    /// fills). Hidden severity is skipped; Info maps to Information.
    /// </summary>
    /// <param name="doc">The SCIP document being built.</param>
    /// <param name="diagnostics">The Roslyn diagnostics of this document.</param>
    /// <param name="source">Diagnostic source tag: compiler or analyzer.</param>
    private static void EmitDiagnostics(
        Scip.Document doc,
        IEnumerable<Diagnostic> diagnostics,
        string source)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Hidden || diagnostic.IsSuppressed)
            {
                continue;
            }

            var span = diagnostic.Location.GetLineSpan();
            if (!span.IsValid)
            {
                continue;
            }

            var occurrence = new Scip.Occurrence
            {
                Range =
                {
                    span.StartLinePosition.Line,
                    span.StartLinePosition.Character,
                    span.EndLinePosition.Line == span.StartLinePosition.Line
                        ? span.EndLinePosition.Character
                        : span.StartLinePosition.Character,
                },
            };
            occurrence.Diagnostics.Add(new Scip.Diagnostic
            {
                Severity = diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => Scip.Severity.Error,
                    DiagnosticSeverity.Warning => Scip.Severity.Warning,
                    _ => Scip.Severity.Information,
                },
                Code = diagnostic.Id,
                Message = diagnostic.GetMessage(),
                Source = source,
            });
            doc.Occurrences.Add(occurrence);
        }
    }
}