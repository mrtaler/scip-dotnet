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
/// silently downgraded.
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
