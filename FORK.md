# Fork notes

This is a fork of [sourcegraph/scip-dotnet](https://github.com/sourcegraph/scip-dotnet),
forked at upstream commit `47884461` ("feat: add .slnx solution format support").

The upstream project is licensed under Apache License 2.0; this fork keeps that
license. See `LICENSE` and `NOTICE`. Per Apache 2.0 §4(b), the modifications made
in this fork are listed below.

## What this fork adds

The fork turns the standalone SCIP indexer into a streaming code-graph feeder
while keeping the emitted `scip.Document` payload byte-for-byte standard SCIP.
Extensions travel as their own message types alongside the canonical stream,
never inside the SCIP messages, so the output stays compatible with any standard
SCIP consumer.

- **gRPC streaming ingest client** (`ScipDotnet/IngestStreamClient.cs`, `ingest.proto`):
  streams typed chunks (session meta → `scip.Document`s → extension batches) to a
  graph loader instead of writing a single buffered `index.scip` file, and renders
  progress from the server's `UploadProgress` stream.
- **Precise CALLS edges**: caller→callee edges collected from the Roslyn semantic
  model in the syntax walkers (calls inside lambdas/local functions are attributed
  to the containing member, matching how a stack trace reads). Carried as an
  extension batch, not in the SCIP bytes.
- **Log-template facts**: `ILogger`/`BeginScope` call sites with their compile-time
  message template (or a `templated=false` marker when the message is built at
  runtime). Extension batch.
- **Diagnostics in the SCIP model**: analyzer/compiler diagnostics populated onto
  `Occurrence.Diagnostics` (an existing SCIP field the upstream indexer left empty),
  gated behind an `--analyzers` flag using `CompilationWithAnalyzers`.
- **Incomplete-compilation flag**: when restore/build fails or the workspace reports
  load failures, the run is marked partial so downstream consumers can warn instead
  of silently presenting a truncated graph.

## Relationship to upstream

`upstream` remote points at `sourcegraph/scip-dotnet`. To pull in upstream changes:

```sh
git fetch upstream
git rebase upstream/main   # or merge
```

The `scip.proto` in this repo carries a local rename (`Descriptor` → `SymbolDescriptor`)
that must be re-applied when refreshing the proto from upstream SCIP; see the header
comment in `scip.proto`.
