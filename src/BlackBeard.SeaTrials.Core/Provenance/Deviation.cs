namespace BlackBeard.SeaTrials.Core.Provenance;

public enum DeviationKind { Added, Removed, Changed, Reordered }

/// <summary>
/// A difference between the request as it stands and its baseline. <c>Path</c> is readable: revisions[after].sha,
/// steps[measure].command, matrix.rounds, compare[0].b. <c>Was</c> and <c>Now</c> are the values as compact JSON;
/// for a reordered list they are the order of the names.
/// </summary>
public sealed record Deviation(
    string Path,
    DeviationKind Kind,
    string? Was,
    string? Now,
    DateTimeOffset At,
    string? Reason = null)
{
    public Deviation WithReason(string? reason) => this with { Reason = reason };

    /// <summary>"path: was -&gt; now", the way the summary lists it. A missing side reads "(none)".</summary>
    public string Describe() => Path + ": " + (Was ?? "(none)") + " \u2192 " + (Now ?? "(none)");
}
