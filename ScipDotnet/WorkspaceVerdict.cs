namespace ScipDotnet;

/// <summary>What a run's workspace diagnostics mean, on the two axes kept apart by <see cref="WorkspaceHealth"/>.</summary>
/// <param name="Incomplete">True when at least one diagnostic indicates a missing or unresolved model.</param>
/// <param name="FirstFailure">The first real failure message, shortened; null when there is none.</param>
/// <param name="Advisories">How many workspace failures were recognised as advisories.</param>
/// <param name="FirstAdvisory">One representative advisory message, shortened; null when there is none.</param>
public sealed record WorkspaceVerdict(
    bool Incomplete,
    string? FirstFailure,
    int Advisories,
    string? FirstAdvisory);
