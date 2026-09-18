using System.Web;
using Codegraph.Ingest;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace ScipDotnet;

/// <summary>
/// Streams one repository index to the codegraph gRPC ingest endpoint as typed
/// chunks: meta first, then canonical scip.Document chunks as they are produced
/// by the indexer (no whole-index accumulation), then the extension batches
/// (log templates, precise call edges). Progress messages from the server are
/// logged so the operator sees honest load percentages.
/// </summary>
public static class IngestStreamClient
{
    private const int ExtensionBatchSize = 2000;

    /// <summary>
    /// Vector inputs carry whole method bodies, so their batch is a quarter of the
    /// other extension batches: five hundred long methods stay well under the 32 MiB
    /// frame cap of the channel.
    /// </summary>
    private const int ChunkBatchSize = 500;

    /// <summary>
    /// Runs the streaming upload session against <paramref name="ingestUrl"/>.
    /// </summary>
    /// <param name="ingestUrl">The h2c ingest endpoint; repo/commit/path/package/declares ride in the query string.</param>
    /// <param name="documents">The indexed documents, produced incrementally.</param>
    /// <param name="options">The index command options (extension side channels, logger).</param>
    /// <param name="workspaceHasFailures">Checked after streaming (workspace diagnostics settle late) to mark the run partial.</param>
    /// <returns>A task that completes when the server confirms the session.</returns>
    public static async Task UploadAsync(
        Uri ingestUrl,
        IAsyncEnumerable<Scip.Document> documents,
        IndexCommandOptions options,
        Func<bool>? workspaceHasFailures = null)
    {
        var query = HttpUtility.ParseQueryString(ingestUrl.Query);
        var meta = new IngestMeta
        {
            Repo = query["repo"] ?? string.Empty,
            Commit = query["commit"] ?? string.Empty,
            Path = query["path"] ?? string.Empty,
            Additive = string.Equals(query["additive"], "true", StringComparison.OrdinalIgnoreCase),
        };
        meta.PackageIds.AddRange(query.GetValues("package") ?? Array.Empty<string>());
        meta.DeclaredPackages.AddRange(query.GetValues("declares") ?? Array.Empty<string>());

        var channelAddress = new UriBuilder(ingestUrl) { Query = string.Empty, Path = string.Empty }.Uri;
        using var channel = GrpcChannel.ForAddress(channelAddress, new GrpcChannelOptions
        {
            MaxSendMessageSize = 32 * 1024 * 1024,
        });
        var client = new CodegraphIngest.CodegraphIngestClient(channel);
        using var call = client.Upload();

        var progressReader = Task.Run(async () =>
        {
            await foreach (var progress in call.ResponseStream.ReadAllAsync())
            {
                if (progress.ExpectedDocuments > 0 && progress.Phase == "documents")
                {
                    var percent = Math.Min(100, progress.DocumentsLoaded * 100 / progress.ExpectedDocuments);
                    options.Logger.LogInformation(
                        "ingest: {Percent}% ({Loaded}/{Expected} documents)",
                        percent, progress.DocumentsLoaded, progress.ExpectedDocuments);
                }
                else
                {
                    options.Logger.LogInformation(
                        "ingest: {Phase} {Loaded} documents {Message}",
                        progress.Phase, progress.DocumentsLoaded, progress.Message);
                }
            }
        });

        await call.RequestStream.WriteAsync(new IngestChunk { Meta = meta });

        var sent = 0;
        await foreach (var document in documents)
        {
            await call.RequestStream.WriteAsync(new IngestChunk { Document = document });
            sent++;
        }

        foreach (var batch in options.LogTemplates.Chunk(ExtensionBatchSize))
        {
            var chunk = new LogTemplateBatch();
            foreach (var fact in batch)
            {
                var proto = new Codegraph.Ingest.LogTemplateFact
                {
                    File = fact.File,
                    Line = fact.Line,
                    Level = fact.Level,
                    Templated = fact.Templated,
                };
                if (fact.Template != null)
                {
                    proto.Template = fact.Template;
                }

                chunk.Facts.Add(proto);
            }

            await call.RequestStream.WriteAsync(new IngestChunk { LogTemplates = chunk });
        }

        foreach (var batch in options.Calls.Chunk(ExtensionBatchSize))
        {
            var chunk = new CallsBatch();
            chunk.Edges.AddRange(batch.Select(fact => new CallEdge
            {
                CallerSymbol = fact.CallerSymbol,
                CalleeSymbol = fact.CalleeSymbol,
                File = fact.File,
                Line = fact.Line,
            }));
            await call.RequestStream.WriteAsync(new IngestChunk { Calls = chunk });
        }

        foreach (var batch in options.Chunks.Chunk(ChunkBatchSize))
        {
            var chunk = new ChunkBatch();
            chunk.Facts.AddRange(batch.Select(fact => new Codegraph.Ingest.ChunkFact
            {
                Symbol = fact.Symbol,
                Aspect = fact.Aspect,
                File = fact.File,
                LineStart = fact.LineStart,
                LineEnd = fact.LineEnd,
                TextHash = fact.TextHash,
                NormalizedText = fact.NormalizedText,
                Ordinal = fact.Ordinal,
                BodyLines = fact.BodyLines,
                MaxNestingDepth = fact.MaxNestingDepth,
                CyclomaticComplexity = fact.CyclomaticComplexity,
            }));
            await call.RequestStream.WriteAsync(new IngestChunk { Chunks = chunk });
        }

        if (workspaceHasFailures?.Invoke() == true && !options.CompilationIncomplete)
        {
            options.CompilationIncomplete = true;
            options.PartialReason = "MSBuild workspace reported load failures (unresolved references)";
        }

        if (options.CompilationIncomplete)
        {
            var trailer = new IngestMeta { Repo = meta.Repo, Partial = true };
            if (options.PartialReason != null)
            {
                trailer.PartialReason = options.PartialReason;
            }

            await call.RequestStream.WriteAsync(new IngestChunk { Meta = trailer });
            options.Logger.LogWarning("ingest: run marked PARTIAL — {Reason}", options.PartialReason);
        }

        await call.RequestStream.CompleteAsync();
        await progressReader;
        options.Logger.LogInformation(
            "ingest: stream finished — {Documents} documents, {Templates} log templates, {Calls} call edges, {Chunks} vector inputs sent",
            sent, options.LogTemplates.Count, options.Calls.Count, options.Chunks.Count);
    }
}
