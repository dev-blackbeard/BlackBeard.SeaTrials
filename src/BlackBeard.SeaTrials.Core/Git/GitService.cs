using System.Globalization;
using System.Text.RegularExpressions;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>
/// What the harness knows how to ask git (SPEC section 5): resolve and pin revisions, list branches, tags and recent
/// commits, fetch, find the clone of a repository, make and remove the worktree of a run, and record the state that
/// the stale-commit guard checks. Every command goes through <see cref="GitRunner"/>, so none outside the allow-list
/// can run, and none changes the user's checkout other than by fetching into it.
/// </summary>
public sealed class GitService
{
    // Between the fields of a git format; git is told "%x1f" (log) or "%1f" (for-each-ref) and prints this character.
    private const char Separator = (char)31;

    private static readonly Regex FullSha = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    private readonly GitRunner _git;

    public GitService(GitRunner git)
    {
        ArgumentNullException.ThrowIfNull(git);
        _git = git;
    }

    public static GitService CreateDefault() => new(new GitRunner());

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var output = await RunOkAsync(Path.GetTempPath(), GitScope.UserClone, cancellationToken, "--version").ConfigureAwait(false);
        return output.Trim();
    }

    // ---- clones -----------------------------------------------------------------------------------------------

    /// <summary>A folder that is inside a git working tree: its top, and the address of "origin". Null for any other folder.</summary>
    public async Task<CloneInfo?> InspectCloneAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Directory.Exists(path))
        {
            return null;
        }

        var top = await _git.RunAsync(new GitInvocation(path, new[] { "rev-parse", "--show-toplevel" }), cancellationToken).ConfigureAwait(false);
        var topText = top.StandardOutput.Trim();
        if (!top.Succeeded || topText.Length == 0)
        {
            return null;
        }

        var toplevel = Path.TrimEndingDirectorySeparator(Path.GetFullPath(topText));
        var origin = await _git.RunAsync(new GitInvocation(toplevel, new[] { "remote", "get-url", "origin" }), cancellationToken).ConfigureAwait(false);
        return new CloneInfo(toplevel, origin.Succeeded ? origin.StandardOutput.Trim() : null);
    }

    /// <summary>The first of the known clones whose "origin" is the repository at <paramref name="remote"/>, or null.</summary>
    public async Task<CloneInfo?> FindCloneAsync(IEnumerable<string> candidates, string remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(remote);
        var wanted = RemoteAddress.Normalize(remote);
        foreach (var candidate in candidates)
        {
            var info = await InspectCloneAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (info?.Origin is { } origin && string.Equals(RemoteAddress.Normalize(origin), wanted, StringComparison.Ordinal))
            {
                return info;
            }
        }

        return null;
    }

    // ---- lists ------------------------------------------------------------------------------------------------

    /// <summary>Branches, remote-tracking branches and tags (a tag is followed to its commit), in that order, by name.</summary>
    public async Task<IReadOnlyList<RefEntry>> ListRefsAsync(string clone, CancellationToken cancellationToken = default)
    {
        // for-each-ref spells a byte as %1f (hex, no x); the log formats below spell it %x1f.
        var format = "--format=%(refname)%1f%(objecttype)%1f%(objectname)%1f%(*objecttype)%1f%(*objectname)";
        var output = await RunOkAsync(
            clone, GitScope.UserClone, cancellationToken, "for-each-ref", format, "refs/heads", "refs/remotes", "refs/tags").ConfigureAwait(false);

        var refs = new List<RefEntry>();
        foreach (var line in Lines(output))
        {
            var fields = line.Split(Separator);
            if (fields.Length != 5)
            {
                continue;
            }

            var name = fields[0];
            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                refs.Add(new RefEntry(RefKind.Branch, name.Substring("refs/heads/".Length), fields[2]));
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal) && !name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                refs.Add(new RefEntry(RefKind.RemoteBranch, name.Substring("refs/remotes/".Length), fields[2]));
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                var tag = name.Substring("refs/tags/".Length);
                if (fields[1] == "commit")
                {
                    refs.Add(new RefEntry(RefKind.Tag, tag, fields[2]));
                }
                else if (fields[1] == "tag" && fields[3] == "commit")
                {
                    refs.Add(new RefEntry(RefKind.Tag, tag, fields[4]));
                }
            }
        }

        return refs
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The newest commits on any branch, remote-tracking branch or tag. (Not "git log --all", which also lists the
    /// commits behind the user's stash.)
    /// </summary>
    public async Task<IReadOnlyList<CommitSummary>> ListRecentCommitsAsync(string clone, int count = 50, CancellationToken cancellationToken = default)
    {
        if (count < 1 || count > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Ask for between 1 and 10000 commits.");
        }

        var result = await _git.RunAsync(
            new GitInvocation(clone, new[]
            {
                "log", "--branches", "--remotes", "--tags", "-n", count.ToString(CultureInfo.InvariantCulture),
                "--format=%H%x1f%an%x1f%aI%x1f%s",
            }),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.StandardError.Contains("does not have any commits", StringComparison.Ordinal))
            {
                return Array.Empty<CommitSummary>();
            }

            throw Failure("log", result);
        }

        return Lines(result.StandardOutput).Select(ParseSummary).ToList();
    }

    // ---- fetch ------------------------------------------------------------------------------------------------

    /// <summary>"git fetch --all --tags --prune": a commit on a throwaway remote branch becomes reachable.</summary>
    public async Task FetchAsync(string clone, CancellationToken cancellationToken = default)
    {
        await RunOkAsync(clone, GitScope.UserClone, cancellationToken, "fetch", "--all", "--tags", "--prune").ConfigureAwait(false);
    }

    // ---- resolve and pin ----------------------------------------------------------------------------------------

    /// <summary>
    /// A branch, a tag, a commit id (full or abbreviated) or any expression git understands, such as "abc1234^", as one
    /// commit. An id that is unknown or ambiguous is an answer, not a guess.
    /// </summary>
    public async Task<Resolution> ResolveAsync(string clone, string rev, CancellationToken cancellationToken = default)
    {
        var problem = RevisionProblem(rev);
        if (problem is not null)
        {
            return new Resolution(rev ?? "", ResolutionStatus.Invalid, null, problem);
        }

        var parsed = await _git.RunAsync(
            new GitInvocation(clone, new[] { "rev-parse", "--verify", rev + "^{commit}" }), cancellationToken).ConfigureAwait(false);
        if (!parsed.Succeeded)
        {
            return parsed.StandardError.Contains("ambiguous", StringComparison.OrdinalIgnoreCase)
                ? new Resolution(rev, ResolutionStatus.Ambiguous, null, "'" + rev + "' matches more than one object; use more characters of the id.")
                : new Resolution(rev, ResolutionStatus.NotFound, null, "No commit matches '" + rev + "' in this clone.");
        }

        var commit = await DescribeAsync(clone, parsed.StandardOutput.Trim(), cancellationToken).ConfigureAwait(false);
        return new Resolution(rev, ResolutionStatus.Found, commit, null);
    }

    /// <summary>
    /// Settles which commit a revision of a request runs (SPEC 10.4). The sha is authoritative: if the clone has it,
    /// it runs, and a rev that now resolves elsewhere is an anomaly to show, never a change. A sha the clone does not
    /// have fails that revision alone. Without a sha, the rev is resolved and pinned.
    /// </summary>
    public async Task<PinResult> PinAsync(string clone, Revision revision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.Sha is null && revision.Rev is null)
        {
            return FailedPin(revision, PinFailure.NothingToPin, "The revision has neither a rev nor a sha.");
        }

        if (revision.Sha is not null)
        {
            return await PinShaAsync(clone, revision, revision.Sha, cancellationToken).ConfigureAwait(false);
        }

        var resolution = await ResolveAsync(clone, revision.Rev!, cancellationToken).ConfigureAwait(false);
        return resolution.Status switch
        {
            ResolutionStatus.Found => new PinResult(
                revision, PinOutcome.PinnedOnOpening, PinFailure.None, resolution.Commit!.Sha, resolution.Commit, Array.Empty<PinAnomaly>(),
                "Pinned on opening: no sha was given."),
            ResolutionStatus.Ambiguous => FailedPin(revision, PinFailure.RevisionAmbiguous, resolution.Message ?? "The rev is ambiguous."),
            ResolutionStatus.Invalid => FailedPin(revision, PinFailure.InvalidRevision, resolution.Message ?? "The rev is not valid."),
            _ => FailedPin(revision, PinFailure.RevisionNotFound, resolution.Message ?? "The rev does not match a commit."),
        };
    }

    private async Task<PinResult> PinShaAsync(string clone, Revision revision, string sha, CancellationToken cancellationToken)
    {
        if (!FullSha.IsMatch(sha))
        {
            return FailedPin(revision, PinFailure.InvalidSha, "The sha must be a full commit id: 40 lower-case hexadecimal digits.");
        }

        var exists = await _git.RunAsync(new GitInvocation(clone, new[] { "cat-file", "-e", sha + "^{commit}" }), cancellationToken).ConfigureAwait(false);
        if (!exists.Succeeded)
        {
            return FailedPin(
                revision, PinFailure.CommitMissing,
                "This clone has no commit " + sha + ". It may not be pushed, or this clone may not have fetched it.");
        }

        var commit = await DescribeAsync(clone, sha, cancellationToken).ConfigureAwait(false);
        var anomalies = new List<PinAnomaly>();
        if (revision.Rev is not null)
        {
            var resolution = await ResolveAsync(clone, revision.Rev, cancellationToken).ConfigureAwait(false);
            switch (resolution.Status)
            {
                case ResolutionStatus.Found when !string.Equals(resolution.Commit!.Sha, sha, StringComparison.Ordinal):
                    anomalies.Add(new PinAnomaly(
                        PinAnomalyKind.RevMovedSinceRequest,
                        "'" + revision.Rev + "' now resolves to " + resolution.Commit.Sha + ", not the requested " + sha + ". The requested commit runs.",
                        resolution.Commit.Sha));
                    break;
                case ResolutionStatus.Ambiguous:
                    anomalies.Add(new PinAnomaly(PinAnomalyKind.RevAmbiguous, "'" + revision.Rev + "' now matches more than one object. The requested commit runs."));
                    break;
                case ResolutionStatus.NotFound:
                case ResolutionStatus.Invalid:
                    anomalies.Add(new PinAnomaly(PinAnomalyKind.RevNoLongerResolves, "'" + revision.Rev + "' does not resolve in this clone any more. The requested commit runs."));
                    break;
            }
        }

        return new PinResult(revision, PinOutcome.Pinned, PinFailure.None, sha, commit, anomalies, null);
    }

    private async Task<ResolvedCommit> DescribeAsync(string clone, string sha, CancellationToken cancellationToken)
    {
        var log = await RunOkAsync(
            clone, GitScope.UserClone, cancellationToken, "log", "-1", "--format=%H%x1f%an%x1f%aI%x1f%s", sha).ConfigureAwait(false);
        var summary = ParseSummary(log.TrimEnd('\r', '\n'));

        var contains = await RunOkAsync(
            clone, GitScope.UserClone, cancellationToken, "for-each-ref", "--contains", sha, "--format=%(refname)", "refs/heads", "refs/remotes")
            .ConfigureAwait(false);
        var branches = new List<string>();
        foreach (var name in Lines(contains))
        {
            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                branches.Add(name.Substring("refs/heads/".Length));
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal) && !name.EndsWith("/HEAD", StringComparison.Ordinal))
            {
                branches.Add(name.Substring("refs/remotes/".Length));
            }
        }

        branches.Sort(StringComparer.Ordinal);
        return new ResolvedCommit(summary.Sha, summary.Subject, summary.AuthorName, summary.AuthorDate, branches);
    }

    // ---- worktrees -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A fresh detached worktree at the pinned commit, at WorkRoot / RepoName / (first 7 of sha)-RunId. A fresh folder
    /// means no stale build output. Submodules are initialised when the repository has any and the request says so.
    /// </summary>
    public async Task<Worktree> AddWorktreeAsync(string clone, WorktreeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!FullSha.IsMatch(request.Sha))
        {
            throw new GitException(GitFailureKind.InvalidArgument, "The sha must be a full commit id: 40 lower-case hexadecimal digits.");
        }

        if (!IsSafeSegment(request.RepoName) || !IsSafeSegment(request.RunId))
        {
            throw new GitException(GitFailureKind.InvalidArgument, "The repository name and the run id must be plain names, without separators.");
        }

        if (!Path.IsPathFullyQualified(request.WorkRoot))
        {
            throw new GitException(GitFailureKind.InvalidArgument, "The work root must be a full path.");
        }

        var path = Path.GetFullPath(Path.Combine(request.WorkRoot, request.RepoName, request.Sha.Substring(0, 7) + "-" + request.RunId));
        if (Directory.Exists(path) || File.Exists(path))
        {
            throw new GitException(GitFailureKind.WorktreeExists, "There is already something at " + path + ".");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await RunOkAsync(clone, GitScope.UserClone, cancellationToken, "worktree", "add", "--detach", path, request.Sha).ConfigureAwait(false);

        if (request.InitSubmodules && File.Exists(Path.Combine(path, ".gitmodules")))
        {
            await RunOkAsync(path, GitScope.OwnWorktree, cancellationToken, "submodule", "update", "--init", "--recursive").ConfigureAwait(false);
        }

        var warnings = new List<string>();
        var longest = LongestRelativePath(path);
        if (path.Length + 1 + longest > request.PathWarningLength)
        {
            warnings.Add(
                "The longest path in this worktree is " + (path.Length + 1 + longest) + " characters, more than " + request.PathWarningLength
                + ". Windows tools may fail on it; use a shorter work root.");
        }

        return new Worktree(path, request.Sha, warnings);
    }

    /// <summary>
    /// Removes a worktree the harness made: "git worktree remove --force", then "git worktree prune". Refuses any path
    /// that is not inside the work root, and the user's own checkout.
    /// </summary>
    public async Task RemoveWorktreeAsync(string clone, string workRoot, string worktreePath, CancellationToken cancellationToken = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        if (!IsStrictlyUnder(workRoot, full))
        {
            throw new GitException(GitFailureKind.OutsideWorkRoot, full + " is not inside the work root " + workRoot + "; it was not touched.");
        }

        var info = await InspectCloneAsync(clone, cancellationToken).ConfigureAwait(false);
        if (info is not null && string.Equals(info.TopLevel, full, PathComparison))
        {
            throw new GitException(GitFailureKind.OutsideWorkRoot, full + " is the user's own checkout; it was not touched.");
        }

        var removed = await _git.RunAsync(new GitInvocation(clone, new[] { "worktree", "remove", "--force", full }), cancellationToken).ConfigureAwait(false);
        if (!removed.Succeeded && Directory.Exists(full))
        {
            // No longer registered with git (a crash, a manual prune): the folder is the harness's own, so clear it.
            ForceDelete(full);
        }

        await PruneWorktreesAsync(clone, cancellationToken).ConfigureAwait(false);
    }

    public async Task PruneWorktreesAsync(string clone, CancellationToken cancellationToken = default)
    {
        await RunOkAsync(clone, GitScope.UserClone, cancellationToken, "worktree", "prune").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorktreeEntry>> ListWorktreesAsync(string clone, CancellationToken cancellationToken = default)
    {
        var output = await RunOkAsync(clone, GitScope.UserClone, cancellationToken, "worktree", "list", "--porcelain").ConfigureAwait(false);
        var entries = new List<WorktreeEntry>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;
        var locked = false;
        var prunable = false;

        void Flush()
        {
            if (path is not null)
            {
                entries.Add(new WorktreeEntry(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), head, branch, detached, locked, prunable, entries.Count == 0));
            }

            path = null;
            head = null;
            branch = null;
            detached = false;
            locked = false;
            prunable = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                Flush();
            }
            else if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line.Substring("worktree ".Length);
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line.Substring("HEAD ".Length);
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                branch = line.Substring("branch ".Length);
            }
            else if (line == "detached")
            {
                detached = true;
            }
            else if (line == "locked" || line.StartsWith("locked ", StringComparison.Ordinal))
            {
                locked = true;
            }
            else if (line == "prunable" || line.StartsWith("prunable ", StringComparison.Ordinal))
            {
                prunable = true;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>"Clean up": removes every worktree under the work root, leaves all others alone, and prunes. Returns how many were removed.</summary>
    public async Task<int> CleanUpWorktreesAsync(string clone, string workRoot, CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var entry in await ListWorktreesAsync(clone, cancellationToken).ConfigureAwait(false))
        {
            if (!entry.IsMain && IsStrictlyUnder(workRoot, entry.Path))
            {
                await RemoveWorktreeAsync(clone, workRoot, entry.Path, cancellationToken).ConfigureAwait(false);
                removed++;
            }
        }

        await PruneWorktreesAsync(clone, cancellationToken).ConfigureAwait(false);
        return removed;
    }

    // ---- the stale-commit guard ----------------------------------------------------------------------------------

    /// <summary>"git rev-parse HEAD" and "git status --porcelain" of a worktree, for <see cref="StaleGuard"/>.</summary>
    public async Task<WorktreeState> CaptureStateAsync(string worktree, CancellationToken cancellationToken = default)
    {
        var head = await RunOkAsync(worktree, GitScope.OwnWorktree, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        var status = await RunOkAsync(worktree, GitScope.OwnWorktree, cancellationToken, "status", "--porcelain").ConfigureAwait(false);
        return new WorktreeState(head.Trim(), Lines(status).ToList());
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private async Task<string> RunOkAsync(string directory, GitScope scope, CancellationToken cancellationToken, params string[] arguments)
    {
        var result = await _git.RunAsync(new GitInvocation(directory, arguments, scope), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Failure(arguments[0], result);
        }

        return result.StandardOutput;
    }

    private static GitException Failure(string command, GitResult result)
    {
        var detail = result.StandardError.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? "no message";
        return new GitException(
            GitFailureKind.CommandFailed,
            "git " + command + " failed (exit " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + detail,
            result.StandardError);
    }

    private static PinResult FailedPin(Revision revision, PinFailure failure, string message) =>
        new(revision, PinOutcome.Failed, failure, null, null, Array.Empty<PinAnomaly>(), message);

    private static IEnumerable<string> Lines(string output) =>
        output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    private static CommitSummary ParseSummary(string line)
    {
        var fields = line.Split(Separator, 4);
        if (fields.Length != 4)
        {
            throw new GitException(GitFailureKind.CommandFailed, "git printed a commit in a form the harness does not understand.");
        }

        return new CommitSummary(
            fields[0], fields[3], fields[1], DateTimeOffset.Parse(fields[2], CultureInfo.InvariantCulture));
    }

    private static string? RevisionProblem(string? rev)
    {
        if (string.IsNullOrWhiteSpace(rev))
        {
            return "The revision is empty.";
        }

        if (rev.StartsWith('-'))
        {
            return "A revision must not start with '-'.";
        }

        return rev.Any(char.IsControl) ? "A revision must not contain control characters." : null;
    }

    private static bool IsSafeSegment(string value) =>
        value.Length > 0
        && value.Length <= 100
        && value != "."
        && value != ".."
        && value == value.Trim()
        && !value.EndsWith('.')
        && value.All(c => c >= ' ' && "/\\:*?\"<>|".IndexOf(c) < 0);

    private static bool IsStrictlyUnder(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var pathFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return pathFull.Length > rootFull.Length
            && pathFull.StartsWith(rootFull, PathComparison)
            && (pathFull[rootFull.Length] == Path.DirectorySeparatorChar || pathFull[rootFull.Length] == Path.AltDirectorySeparatorChar);
    }

    private static int LongestRelativePath(string root)
    {
        var longest = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, ".git", StringComparison.Ordinal))
                {
                    continue;
                }

                longest = Math.Max(longest, entry.Length - root.Length - 1);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
            }
        }

        return longest;
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
