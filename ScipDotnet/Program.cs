using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Hosting;
using System.CommandLine.NamingConventionBinder;
using System.CommandLine.Parsing;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScipDotnet;

public static class Program
{
    private const int DotnetRestoreTimeout = 300000;

    public static async Task<int> Main(string[] args)
    {
        var indexCommand = new Command("index", "Index a solution file")
        {
            new Argument<FileInfo>("projects", "Path to the .sln/.slnx (solution) or .csproj/.vbproj file")
                { Arity = ArgumentArity.ZeroOrMore },
            new Option<string>("--output", () => "index.scip",
                "Path to the output SCIP index file, or '-' to stream the index to stdout"),
            new Option<Uri?>("--output-url", () => null,
                "POST the SCIP index bytes to this URL instead of writing a file. " +
                "When --output is also a file path, a local copy is written as well."),
            new Option<Uri?>("--ingest-url", () => null,
                "Stream the index to this codegraph gRPC ingest endpoint (h2c) as typed chunks: " +
                "documents are sent AS THEY ARE INDEXED (no in-memory accumulation), log templates " +
                "and call edges follow as extension batches. Query parameters repo/commit/path/" +
                "package/declares are forwarded as the session meta. Takes precedence over --output-url."),
            new Option<FileInfo>("--working-directory",
                () => new FileInfo(Directory.GetCurrentDirectory()),
                "The working directory"),
            new Option<List<string>>("--include", () => new List<string>(),
                "Only index files that match the given file glob pattern")
            {
                Arity = ArgumentArity.ZeroOrMore,
            },
            new Option<List<string>>("--exclude", () => new List<string>(),
                "Only index files that match the given file glob pattern")
            {
                Arity = ArgumentArity.ZeroOrMore
            },
            new Option<bool>("--allow-global-symbol-definitions", () => false,
                "If enabled, allow public symbol definitions to be accessible from other SCIP indexes. " +
                "If disabled, then public symbols will only be visible within the index."),
            new Option<int>("--dotnet-restore-timeout", () => DotnetRestoreTimeout,
                @"The timeout (in ms) for the ""dotnet restore"" command"),
            new Option<bool>("--skip-dotnet-restore", () => false,
                @"Skip executing ""dotnet restore"" and assume it has been run externally."),
            new Option<bool>("--use-build", () => false,
                @"Run ""dotnet build -p:EmitCompilerGeneratedFiles=true"" instead of ""dotnet restore"" before indexing, " +
                @"so source-generated code (e.g. API clients) is materialized and indexed."),
            new Option<FileInfo?>("--nuget-config-path", () => null,
                @"Provide a case sensitive custom path for ""dotnet restore"" to find the NuGet.config file. " +
                @"If not provided, ""dotnet restore"" will search for the NuGet.config file recursively up the folder hierarchy " +
                @"and in the default user and system config locations."),
            new Option<bool>("--analyzers", () => false,
                "Also run the projects' configured Roslyn analyzers (StyleCop/Sonar/etc.) and emit their " +
                "diagnostics into the SCIP index. Compiler diagnostics are always emitted; analyzer runs " +
                "are noticeably slower, hence opt-in."),
            new Option<List<string>>("--property", () => new List<string>(),
                "MSBuild property as Key=Value, applied to both the restore/build step and the " +
                "MSBuildWorkspace evaluation (e.g. --property EnableWindowsTargeting=true). Repeatable.")
            {
                Arity = ArgumentArity.ZeroOrMore
            },
        };
        indexCommand.Handler = CommandHandler.Create(IndexCommandHandler.Process);
        var rootCommand =
            new RootCommand(
                "SCIP indexer for the C# and Visual basic programming languages. Built with the Roslyn .NET compiler. Supports MSBuild.")
            {
                indexCommand,
            };
        var builder = new CommandLineBuilder(rootCommand);
        return await builder.UseHost(_ => Host.CreateDefaultBuilder(), host =>
            {
                host.ConfigureAppConfiguration(b => b.AddInMemoryCollection());
                host.ConfigureLogging(b =>
                {
                    b.AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = true;
                        options.SingleLine = true;
                        options.TimestampFormat = "HH:mm:ss ";
                    }).AddFilter("Microsoft.Hosting.Lifetime", LogLevel.None);
                    b.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(
                        options => options.LogToStandardErrorThreshold = LogLevel.Trace);
                });
                host.ConfigureServices((_, collection) =>
                    collection
                        .AddSingleton(_ => CreateWorkspace())
                        .AddTransient<ScipProjectIndexer>()
                        .AddTransient(services => (Workspace)services.GetRequiredService<MSBuildWorkspace>())
                );
            })
            .UseDefaults()
            .Build()
            .InvokeAsync(args);
    }

    /// <summary>
    /// Creates the MSBuild workspace with the global properties collected from
    /// --property options. Properties must be passed at Create time: the workspace
    /// evaluates them BEFORE importing SDK targets, so setting e.g.
    /// EnableWindowsTargeting inside a csproj is already too late for evaluation
    /// on non-Windows hosts.
    /// </summary>
    /// <returns>The configured MSBuild workspace.</returns>
    private static MSBuildWorkspace CreateWorkspace()
    {
        MSBuildLocator.RegisterDefaults();
        return MSBuildWorkspace.Create(WorkspaceGlobalProperties.Values);
    }
}

/// <summary>
/// Holds the MSBuild global properties parsed from --property Key=Value options.
/// The workspace is resolved lazily from DI after command parsing, so the handler
/// fills this holder before the first resolution.
/// </summary>
public static class WorkspaceGlobalProperties
{
    /// <summary>Gets the property map applied to the MSBuild workspace.</summary>
    public static Dictionary<string, string> Values { get; } = new();
}