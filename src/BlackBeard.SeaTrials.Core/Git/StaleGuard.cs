namespace BlackBeard.SeaTrials.Core.Git;

public enum GuardVerdict
{
    Ok,

    /// <summary>HEAD is not the pinned commit. Before the first step nothing is run; after the last the run is not trusted.</summary>
    Stale,

    /// <summary>The worktree is not clean before the first step.</summary>
    NotClean,
}

public sealed record GuardResult(GuardVerdict Verdict, string Message, IReadOnlyList<string> Warnings)
{
    public bool IsOk => Verdict == GuardVerdict.Ok;
}

/// <summary>
/// The stale-commit guard (SPEC section 5.3), the reason the app exists. Before the first step and after the last the
/// harness records HEAD and the status of the worktree. HEAD must be the pinned commit and the worktree must be clean
/// before anything runs; afterwards, changes to tracked files are a warning, and a HEAD that moved is not trusted.
/// </summary>
public static class StaleGuard
{
    public static GuardResult CheckBeforeFirstStep(WorktreeState state, string pinnedSha)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pinnedSha);
        if (!string.Equals(state.HeadSha, pinnedSha, StringComparison.Ordinal))
        {
            return new GuardResult(
                GuardVerdict.Stale,
                "The worktree is at " + state.HeadSha + " but the run is pinned to " + pinnedSha + ". Nothing was run.",
                Array.Empty<string>());
        }

        if (state.StatusLines.Count > 0)
        {
            return new GuardResult(
                GuardVerdict.NotClean,
                "The worktree is not clean before the first step (" + state.StatusLines.Count + " entries). Nothing was run.",
                Array.Empty<string>());
        }

        return new GuardResult(GuardVerdict.Ok, "The worktree is at the pinned commit and clean.", Array.Empty<string>());
    }

    public static GuardResult CheckAfterLastStep(WorktreeState state, string pinnedSha)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(pinnedSha);
        if (!string.Equals(state.HeadSha, pinnedSha, StringComparison.Ordinal))
        {
            return new GuardResult(
                GuardVerdict.Stale,
                "HEAD moved during the run: it is " + state.HeadSha + " but the run was pinned to " + pinnedSha + ". The results are not from the pinned commit.",
                Array.Empty<string>());
        }

        var warnings = state.TrackedChanges
            .Select(line => "A tracked file changed during the run: " + line.Trim())
            .ToList();
        return new GuardResult(GuardVerdict.Ok, "HEAD is still the pinned commit.", warnings);
    }
}
