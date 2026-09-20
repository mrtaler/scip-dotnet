using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ScipDotnet;

namespace ScipDotnet.Tests;

/// <summary>
/// Pins the chunker's SIZE GATES. The gates decide how a method is cut into vector inputs, and
/// they are deliberately measured in TOKENS: a threshold on physical lines moves when someone
/// reformats a file or inserts a blank line, which silently re-cuts methods nobody edited and
/// invalidates their vectors. These tests are characterization tests — they record the boundary
/// as it is, so a future change to it has to be a decision rather than an accident.
/// </summary>
public class MethodChunkerTests
{
    /// <summary>Parses one member and returns its chunk drafts.</summary>
    /// <param name="member">A method or constructor declaration, as source.</param>
    /// <returns>The drafts the chunker produces for it.</returns>
    private static IReadOnlyList<ChunkDraft> Collect(string member)
    {
        var tree = CSharpSyntaxTree.ParseText("class Holder\n{\n" + member + "\n}\n");
        var node = tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Single();
        return MethodChunker.Collect(node);
    }

    /// <summary>A body long enough to cross the block threshold, built from N assignments.</summary>
    /// <param name="statements">How many statements to emit inside the try block.</param>
    /// <returns>The member source.</returns>
    private static string MethodWithTryBlock(int statements)
    {
        var lines = string.Join(
            "\n",
            Enumerable.Range(0, statements).Select(i => $"            var value{i} = {i} + Compute({i});"));
        return "    public int Work(int seed)\n"
             + "    {\n"
             + "        try\n"
             + "        {\n"
             + lines + "\n"
             + "            return seed;\n"
             + "        }\n"
             + "        catch (InvalidOperationException)\n"
             + "        {\n"
             + "            return -1;\n"
             + "        }\n"
             + "    }\n";
    }

    [Test]
    public void SignatureAndBodyAreAlwaysProducedForAMethodWithABody()
    {
        var drafts = Collect("    public int Add(int a, int b) { return a + b; }");

        Assert.That(drafts.Select(d => d.Aspect), Is.EquivalentTo(new[] { "signature", "body" }));
        Assert.That(drafts.Single(d => d.Aspect == "body").BodyStatements, Is.EqualTo(1));
    }

    [Test]
    public void AnAbstractMethodYieldsOnlyItsSignature()
    {
        var tree = CSharpSyntaxTree.ParseText("abstract class Holder\n{\n    public abstract int Add(int a, int b);\n}\n");
        var node = tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>().Single();

        var drafts = MethodChunker.Collect(node);

        Assert.That(drafts.Select(d => d.Aspect), Is.EqualTo(new[] { "signature" }));
    }

    [Test]
    public void ABodyBelowTheTokenThresholdIsNotCutIntoBlocks()
    {
        var drafts = Collect(MethodWithTryBlock(2));
        var body = drafts.Single(d => d.Aspect == "body");

        Assert.That(body.BodyTokens, Is.LessThan(MethodChunker.BlockThresholdTokens));
        Assert.That(drafts.Any(d => d.Aspect == "block"), Is.False);
    }

    [Test]
    public void ABodyAboveTheThresholdIsCutIntoItsNonDominantBlocks()
    {
        var half = string.Join(
            "\n",
            Enumerable.Range(0, 20).Select(i => $"            var value{i} = {i} + Compute({i});"));
        var drafts = Collect(
            "    public int Work(int seed)\n"
          + "    {\n"
          + "        if (seed > 0)\n"
          + "        {\n"
          + half + "\n"
          + "        }\n"
          + "\n"
          + "        if (seed < 0)\n"
          + "        {\n"
          + half.Replace("value", "other") + "\n"
          + "        }\n"
          + "\n"
          + "        return seed;\n"
          + "    }\n");

        var body = drafts.Single(d => d.Aspect == "body");
        var blocks = drafts.Where(d => d.Aspect == "block").ToList();

        Assert.That(body.BodyTokens, Is.GreaterThan(MethodChunker.BlockThresholdTokens));
        Assert.That(blocks, Has.Count.GreaterThanOrEqualTo(2), "two comparable branches are two logical units");
        Assert.That(blocks.Select(b => b.Ordinal), Is.Unique, "block ordinals number the units within the method");
        Assert.That(
            blocks.All(b => b.NormalizedText.Length > 0 && b.TextHash != body.TextHash),
            Is.True);
    }

    [Test]
    public void ASingleBlockThatDominatesTheBodyIsNotEmittedAsItsOwnUnit()
    {
        var drafts = Collect(MethodWithTryBlock(40));

        Assert.That(
            drafts.Single(d => d.Aspect == "body").BodyTokens,
            Is.GreaterThan(MethodChunker.BlockThresholdTokens),
            "the body is over the threshold, so the split was attempted");
        Assert.That(
            drafts.Any(d => d.Aspect == "block"),
            Is.False,
            "one try that IS the method would duplicate the body vector, so the dominance rule drops it");
    }

    [Test]
    public void TheBlockThresholdIsCrossedByCODE_NotByFormatting()
    {
        var compact = MethodWithTryBlock(40);
        var reformatted = compact.Replace("\n", "\n\n");

        var compactDrafts = Collect(compact);
        var reformattedDrafts = Collect(reformatted);

        Assert.That(
            reformattedDrafts.Select(d => d.TextHash),
            Is.EqualTo(compactDrafts.Select(d => d.TextHash)),
            "a blank line after every line must not change a single chunk");
        Assert.That(
            reformattedDrafts.Single(d => d.Aspect == "body").BodyTokens,
            Is.EqualTo(compactDrafts.Single(d => d.Aspect == "body").BodyTokens),
            "tokens exclude trivia, so the gate cannot move");
        Assert.That(
            reformattedDrafts.Single(d => d.Aspect == "body").BodyLines,
            Is.GreaterThan(compactDrafts.Single(d => d.Aspect == "body").BodyLines),
            "physical lines DO move — which is exactly why they are informational only");
    }

    [Test]
    public void CommentsChangeNeitherTheTextNorTheSize()
    {
        var bare = "    public int Add(int a, int b) { return a + b; }";
        var commented = "    public int Add(int a, int b)\n    {\n        // explain the sum\n        return a + b; /* trailing */\n    }";

        var bareBody = Collect(bare).Single(d => d.Aspect == "body");
        var commentedBody = Collect(commented).Single(d => d.Aspect == "body");

        Assert.That(commentedBody.TextHash, Is.EqualTo(bareBody.TextHash));
        Assert.That(commentedBody.BodyTokens, Is.EqualTo(bareBody.BodyTokens));
    }

    [Test]
    public void ABlockThatIsTheWholeBodyIsNotEmittedTwice()
    {
        var drafts = Collect(
            "    public int Work(int seed)\n"
          + "    {\n"
          + "        if (seed > 0)\n"
          + "        {\n"
          + string.Join("\n", Enumerable.Range(0, 40).Select(i => $"            var value{i} = {i} + Compute({i});")) + "\n"
          + "            return seed;\n"
          + "        }\n"
          + "\n"
          + "        return 0;\n"
          + "    }\n");

        var body = drafts.Single(d => d.Aspect == "body");
        var blocks = drafts.Where(d => d.Aspect == "block").ToList();

        Assert.That(body.BodyTokens, Is.GreaterThan(MethodChunker.BlockThresholdTokens));
        Assert.That(
            blocks.Any(b => b.TextHash == body.TextHash),
            Is.False,
            "a block that dominates its body duplicates the body vector and must be suppressed");
    }

    [Test]
    public void TokenCountIgnoresTriviaWhileStatementCountCountsStatements()
    {
        var tree = CSharpSyntaxTree.ParseText(
            "class Holder\n{\n    void M()\n    {\n\n        // a comment\n        var a = 1;\n        var b = 2;\n    }\n}\n");
        var method = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        var spaced = CSharpSyntaxTree.ParseText(
            "class Holder\n{\n    void M()\n    {\n        var a  =  1;   /* noise */\n        var b = 2;\n    }\n}\n");
        var spacedMethod = spaced.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();

        Assert.That(MethodChunker.StatementCount(method.Body!), Is.EqualTo(2));
        Assert.That(
            MethodChunker.TokenCount(spacedMethod.Body!),
            Is.EqualTo(MethodChunker.TokenCount(method.Body!)),
            "extra whitespace and a comment are trivia, so the token count must not budge");
    }
}
