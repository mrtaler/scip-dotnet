using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace ScipDotnet;

/// <summary>
/// Splits MSBuildWorkspace diagnostics into the two things a reader needs kept apart:
/// INCOMPLETENESS (the Roslyn model is missing types, so absence of a symbol proves
/// nothing) and RUN ADVISORIES (the model is complete, something merely deserves a look).
/// </summary>
/// <remarks>
/// Both used to collapse into one flag, and the cost was measured: the .NET SDK reports its
/// package-pruning advisory ("PackageReference X will not be pruned…", the NU1510 family)
/// through the workspace's failure channel, which marked a repository partial on 13 runs in
/// one day while its graph held every symbol. A health flag that is permanently on is a
/// health flag nobody reads — the same erosion as an always-red build.
/// <para>
/// The advisory list is deliberately narrow and message-based: a workspace failure is
/// treated as benign ONLY when it matches a known advisory. Anything unrecognised keeps its
/// old meaning and still marks the run partial, so a new kind of real breakage is never
/// silently downgraded. Matching is on the MESSAGE and not on the NuGet code because the
/// workspace does not put the code in the text it hands over — read one of these messages
/// and there is no NU1510 in it to match.
/// </para>
/// <para>
/// The four families here came from the distinct failure messages actually present in the
/// fleet's index logs, not from taste. What was deliberately LEFT as incompleteness: XAML
/// markup errors (a failed markup compile can drop generated partial members from the C#
/// model) and missing project references (types really are unresolved).
/// </para>
/// </remarks>
public static class WorkspaceHealth
{
    /// <summary>
    /// Message patterns that are advisories despite arriving as workspace FAILURES.
    /// </summary>
    private static readonly Regex[] Advisories =
    [
        // .NET 10 SDK package pruning (NU1510): "PackageReference System.Formats.Asn1 will
        // not be pruned. Consider removing this package from your dependencies…"
        new(@"will not be pruned", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // NuGet version fallback (NU1603): "X depends on Y (>= 2.72.0) but Y 2.72.0 was not
        // found. Y 2.76.0 was resolved instead." A HIGHER version was substituted, so the
        // model is complete; the phrase that identifies it is the substitution, not the
        // "not found", because a genuinely missing package never says what replaced it.
        new(@"was resolved instead", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // NuGet audit (NU1901-NU1904): "Package 'X' 9.0.1 has a known high severity
        // vulnerability…". A security finding about a dependency, decided earlier to be run
        // health rather than incompleteness: the types resolve, the package needs replacing.
        new(@"has a known .*? severity vulnerability", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // NuGet TFM fallback (NU1701): "Package 'X' was restored using '.NETFramework…'
        // instead of the project target framework". The assembly still loads and its types
        // resolve; this is the normal shape of an old dependency in a modern project.
        new(@"was restored using '\.NETFramework", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>Classifies a workspace's diagnostics.</summary>
    /// <param name="diagnostics">Everything the workspace reported during the run.</param>
    /// <returns>The verdict: whether the model is incomplete, the first real failure, and the advisory count with a sample.</returns>
    public static WorkspaceVerdict Classify(IEnumerable<WorkspaceDiagnostic> diagnostics)
    {
        string? firstFailure = null;
        string? firstAdvisory = null;
        var advisories = 0;

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
            {
                continue;
            }

            if (IsAdvisory(diagnostic.Message))
            {
                advisories++;
                firstAdvisory ??= Shorten(diagnostic.Message);
                continue;
            }

            firstFailure ??= Shorten(diagnostic.Message);
        }

        return new WorkspaceVerdict(firstFailure is not null, firstFailure, advisories, firstAdvisory);
    }

    /// <summary>Tells whether one workspace message is a known advisory rather than incompleteness.</summary>
    /// <param name="message">The workspace diagnostic message.</param>
    /// <returns>True when the message matches a known advisory family.</returns>
    public static bool IsAdvisory(string message) => Advisories.Any(pattern => pattern.IsMatch(message));

    private static string Shorten(string message)
    {
        var single = message.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return single.Length <= 300 ? single : single[..300] + "…";
    }
}
