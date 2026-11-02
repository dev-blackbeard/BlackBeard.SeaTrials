using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>Where a git command runs. Some commands are allowed only in a worktree that the harness made itself.</summary>
public enum GitScope
{
    /// <summary>The user's own clone: read it and fetch, nothing else.</summary>
    UserClone,

    /// <summary>A worktree the harness created for a run.</summary>
    OwnWorktree,
}

/// <summary>One git command: the directory it runs in, its arguments (without "git") and its scope.</summary>
public sealed record GitInvocation(string WorkingDirectory, IReadOnlyList<string> Arguments, GitScope Scope = GitScope.UserClone);

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public enum GitFailureKind
{
    GitNotFound,
    CommandFailed,
    InvalidArgument,
    OutsideWorkRoot,
    WorktreeExists,
    Timeout,
}

/// <summary>Something went wrong that the caller cannot treat as an ordinary answer ("no such revision" is an answer).</summary>
public class GitException : Exception
{
    public GitException(GitFailureKind kind, string message, string? output = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Output = output;
    }

    public GitFailureKind Kind { get; }

    /// <summary>What git printed on its error output, when there was a command.</summary>
    public string? Output { get; }
}

/// <summary>The harness tried to run a git command that is not on the list of section 5.4.</summary>
public sealed class GitCommandNotAllowedException : InvalidOperationException
{
    public GitCommandNotAllowedException(IReadOnlyList<string> arguments, string reason)
        : base("git " + string.Join(" ", arguments) + " is not allowed: " + reason)
    {
        Arguments = arguments;
        Reason = reason;
    }

    public IReadOnlyList<string> Arguments { get; }

    public string Reason { get; }
}

public enum RefKind { Branch, RemoteBranch, Tag }

/// <summary>A branch, a remote-tracking branch or a tag, with the commit it points to (a tag is followed to its commit).</summary>
public sealed record RefEntry(RefKind Kind, string Name, string Sha);

public sealed record CommitSummary(string Sha, string Subject, string AuthorName, DateTimeOffset AuthorDate);

/// <summary>A commit as the window shows it under the revision box: id, subject, author, date and the branches that contain it.</summary>
public sealed record ResolvedCommit(
    string Sha,
    string Subject,
    string AuthorName,
    DateTimeOffset AuthorDate,
    IReadOnlyList<string> Branches);

public enum ResolutionStatus { Found, NotFound, Ambiguous, Invalid }

public sealed record Resolution(string Rev, ResolutionStatus Status, ResolvedCommit? Commit, string? Message);

/// <summary>A clone on this PC: the top of its working tree and the address of its "origin", when it has one.</summary>
public sealed record CloneInfo(string TopLevel, string? Origin);

public enum PinOutcome
{
    /// <summary>The request's sha is in the clone and is what will run.</summary>
    Pinned,

    /// <summary>The request had no sha; the rev was resolved on opening and pinned.</summary>
    PinnedOnOpening,

    /// <summary>This revision cannot run. The other revisions of the request still can.</summary>
    Failed,
}

public enum PinFailure
{
    None,
    NothingToPin,
    InvalidSha,
    InvalidRevision,
    CommitMissing,
    RevisionNotFound,
    RevisionAmbiguous,
}

public enum PinAnomalyKind
{
    /// <summary>The rev now resolves to a different commit than the sha (a branch moved since the request was written).</summary>
    RevMovedSinceRequest,

    /// <summary>The rev does not resolve any more (a branch was deleted); the sha still runs.</summary>
    RevNoLongerResolves,

    /// <summary>The rev is an abbreviated id that now matches more than one object.</summary>
    RevAmbiguous,
}

/// <summary>Something to show and record about a revision that does not stop it from running.</summary>
public sealed record PinAnomaly(PinAnomalyKind Kind, string Message, string? RevResolvesTo = null);

/// <summary>What happened when a revision of a request was resolved against a clone.</summary>
public sealed record PinResult(
    Revision Revision,
    PinOutcome Outcome,
    PinFailure Failure,
    string? Sha,
    ResolvedCommit? Commit,
    IReadOnlyList<PinAnomaly> Anomalies,
    string? Message)
{
    public bool CanRun => Outcome != PinOutcome.Failed;
}

/// <summary>What a worktree for one run is made of. The path is WorkRoot / RepoName / (first 7 of Sha)-RunId.</summary>
public sealed record WorktreeRequest(
    string WorkRoot,
    string RepoName,
    string Sha,
    string RunId,
    bool InitSubmodules = true,
    int PathWarningLength = 200);

public sealed record Worktree(string Path, string Sha, IReadOnlyList<string> Warnings);

public sealed record WorktreeEntry(
    string Path,
    string? HeadSha,
    string? Branch,
    bool Detached,
    bool Locked,
    bool Prunable,
    bool IsMain);

/// <summary>HEAD and "git status --porcelain" of a worktree at one moment.</summary>
public sealed record WorktreeState(string HeadSha, IReadOnlyList<string> StatusLines)
{
    /// <summary>Changes to tracked files. Untracked files (build output that is not ignored) do not count.</summary>
    public IReadOnlyList<string> TrackedChanges =>
        StatusLines.Where(line => !line.StartsWith("??", StringComparison.Ordinal)).ToList();
}
