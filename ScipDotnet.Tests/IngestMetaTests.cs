using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging.Abstractions;
using ScipDotnet;

namespace ScipDotnet.Tests;

/// <summary>
/// Pins the session header of the ingest stream. The server acts on it BEFORE the first document
/// arrives — it decides what to wipe — so a field silently dropped here is a data loss that no
/// later message can undo.
/// </summary>
public class IngestMetaTests
{
    /// <summary>Builds options whose only interesting field is the analyzers flag.</summary>
    /// <param name="analyzers">Whether this run ran the analyzers.</param>
    /// <returns>Options usable for header construction.</returns>
    private static IndexCommandOptions Options(bool analyzers) => new(
        new FileInfo("/tmp/work"),
        new FileInfo("/tmp/index.scip"),
        new List<FileInfo>(),
        NullLogger<IndexCommandOptions>.Instance,
        new Matcher(),
        AllowGlobalSymbolDefinitions: false,
        DotnetRestoreTimeout: 0,
        SkipDotnetRestore: true,
        NugetConfigPath: null,
        UseBuild: false,
        Analyzers: analyzers);

    [Test]
    public void TheHeaderCarriesRepoCommitAndPathFromTheQuery()
    {
        var meta = IngestStreamClient.BuildMeta(
            new Uri("http://localhost:8304/?repo=group%2Fdemo&commit=abc1234&path=src/Demo.sln"),
            Options(analyzers: false));

        Assert.That(meta.Repo, Is.EqualTo("group/demo"));
        Assert.That(meta.Commit, Is.EqualTo("abc1234"));
        Assert.That(meta.Path, Is.EqualTo("src/Demo.sln"));
    }

    [Test]
    public void AnalyzersComeFromTheRunNotFromTheUrl()
    {
        var url = new Uri("http://localhost:8304/?repo=group%2Fdemo");

        Assert.That(IngestStreamClient.BuildMeta(url, Options(analyzers: true)).Analyzers, Is.True);
        Assert.That(IngestStreamClient.BuildMeta(url, Options(analyzers: false)).Analyzers, Is.False);
    }

    [Test]
    public void AdditiveIsOffUnlessTheQuerySaysTrue()
    {
        var options = Options(analyzers: false);

        Assert.That(IngestStreamClient.BuildMeta(new Uri("http://localhost:8304/?repo=r"), options).Additive, Is.False);
        Assert.That(IngestStreamClient.BuildMeta(new Uri("http://localhost:8304/?repo=r&additive=TRUE"), options).Additive, Is.True);
        Assert.That(IngestStreamClient.BuildMeta(new Uri("http://localhost:8304/?repo=r&additive=yes"), options).Additive, Is.False);
    }

    [Test]
    public void RepeatedPackageAndDeclaresValuesAllSurvive()
    {
        var meta = IngestStreamClient.BuildMeta(
            new Uri("http://localhost:8304/?repo=r&package=A&package=B&declares=C%2F1.0&declares=D%2F2.0!meta"),
            Options(analyzers: false));

        Assert.That(meta.PackageIds, Is.EqualTo(new[] { "A", "B" }));
        Assert.That(meta.DeclaredPackages, Is.EqualTo(new[] { "C/1.0", "D/2.0!meta" }));
    }

    [Test]
    public void AMissingQueryYieldsEmptyStringsRatherThanNulls()
    {
        var meta = IngestStreamClient.BuildMeta(new Uri("http://localhost:8304/"), Options(analyzers: false));

        Assert.That(meta.Repo, Is.Empty);
        Assert.That(meta.Commit, Is.Empty);
        Assert.That(meta.Path, Is.Empty);
        Assert.That(meta.PackageIds, Is.Empty);
    }
}
