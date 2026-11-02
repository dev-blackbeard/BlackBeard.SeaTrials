using System.Security.Cryptography;
using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Requests;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

/// <summary>
/// SPEC 5.4 as a whole: a complete use of the service, with every git process it starts recorded. Whatever the
/// service does, only the listed commands run, the dangerous ones never do, and the user's checkout ends as it began.
/// </summary>
public class AllowListScenarioTests : GitTestBase
{
    private sealed class SpyLauncher : IGitProcessLauncher
    {
        private readonly SystemGitProcessLauncher _inner = new();
        private readonly object _gate = new();
        private readonly List<GitLaunch> _launches = new();

        public IReadOnlyList<GitLaunch> Launches
        {
            get
            {
                lock (_gate)
                {
                    return _launches.ToList();
                }
            }
        }

        public async Task<GitResult> LaunchAsync(GitLaunch launch, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _launches.Add(launch);
            }

            return await _inner.LaunchAsync(launch, cancellationToken);
        }
    }

    // What a harness must not change in the user's checkout. (Remote-tracking branches and the config are not here:
    // a fetch moves the first, and initialising submodules in a worktree adds an entry to the second.)
    private static string Snapshot(TestRepo repo)
    {
        var index = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index"))));
        return string.Join(
            "\n--\n",
            repo.Run("rev-parse", "HEAD"),
            repo.Run("rev-parse", "--abbrev-ref", "HEAD"),
            repo.Run("status", "--porcelain"),
            repo.Run("for-each-ref", "refs/heads", "refs/tags"),
            repo.Run("reflog"),
            index,
            File.ReadAllText(Path.Combine(repo.Path, "readme.txt")),
            File.ReadAllText(Path.Combine(repo.Path, "scratch.txt")));
    }

    [Fact]
    public async Task OnlyListedCommandsRunAndTheUsersCheckoutIsUntouchedAfterAWholeRun()
    {
        var spy = new SpyLauncher();
        var service = TestGit.Service(spy);
        var workRoot = Folder.Combine("work");

        var library = NewRepo("library");
        library.Commit("lib.txt", "library file", "library commit");
        var (remote, clone) = NewRemoteAndClone();
        clone.Run("-c", "protocol.file.allow=always", "submodule", "add", "-q", library.Path, "lib");
        clone.Run("commit", "-q", "-m", "add the submodule");
        clone.Run("push", "-q", "origin", "main");
        var pinned = clone.Sha();
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        other.Commit("b.txt", "newer", "pushed by somebody else");
        other.Run("push", "-q", "origin", "main");
        File.WriteAllText(Path.Combine(clone.Path, "readme.txt"), "the user's work in progress");
        File.WriteAllText(Path.Combine(clone.Path, "scratch.txt"), "not under version control");
        var before = Snapshot(clone);

        await service.GetVersionAsync();
        (await service.InspectCloneAsync(clone.Path)).ShouldNotBeNull();
        (await service.FindCloneAsync(new[] { clone.Path }, remote.Path)).ShouldNotBeNull();
        await service.ListRefsAsync(clone.Path);
        await service.ListRecentCommitsAsync(clone.Path);
        await service.FetchAsync(clone.Path);
        (await service.ResolveAsync(clone.Path, "origin/main")).Status.ShouldBe(ResolutionStatus.Found);
        (await service.ResolveAsync(clone.Path, "--output=/tmp/never-written")).Status.ShouldBe(ResolutionStatus.Invalid);
        (await service.PinAsync(clone.Path, new Revision("moved", "origin/main", pinned))).Anomalies.Count.ShouldBe(1);
        (await service.PinAsync(clone.Path, new Revision("tip", "main", null))).CanRun.ShouldBeTrue();
        var tree = await service.AddWorktreeAsync(clone.Path, new WorktreeRequest(workRoot, "SampleRepo", pinned, "run1"));
        File.ReadAllText(Path.Combine(tree.Path, "lib", "lib.txt")).ShouldBe("library file");
        StaleGuard.CheckBeforeFirstStep(await service.CaptureStateAsync(tree.Path), pinned).IsOk.ShouldBeTrue();
        (await service.ListWorktreesAsync(clone.Path)).Count.ShouldBe(2);
        await service.RemoveWorktreeAsync(clone.Path, workRoot, tree.Path);
        await service.CleanUpWorktreesAsync(clone.Path, workRoot);
        await service.PruneWorktreesAsync(clone.Path);

        var launches = spy.Launches;
        launches.Count.ShouldBeGreaterThan(15);
        foreach (var launch in launches)
        {
            GitAllowList.IsAllowed(launch.Arguments, launch.Scope).ShouldBeTrue(string.Join(" ", launch.Arguments));
        }

        var commands = launches.Select(launch => launch.Arguments[0]).Distinct().ToList();
        commands.Except(new[] { "--version", "rev-parse", "log", "for-each-ref", "status", "fetch", "worktree", "cat-file", "submodule", "remote" })
            .ShouldBeEmpty();
        foreach (var forbidden in new[] { "checkout", "reset", "clean", "stash", "pull", "merge", "rebase", "commit", "add", "rm", "push", "config" })
        {
            commands.ShouldNotContain(forbidden);
        }

        commands.ShouldContain("submodule"); // the scenario did exercise the worktree-only command
        launches.Where(launch => launch.Scope == GitScope.UserClone)
            .Any(launch => launch.Arguments[0] == "status" || launch.Arguments[0] == "submodule").ShouldBeFalse();
        launches.Where(launch => launch.Scope == GitScope.OwnWorktree)
            .All(launch => Path.GetFullPath(launch.WorkingDirectory).StartsWith(Path.GetFullPath(workRoot), StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();
        launches.Any(launch => launch.Arguments.Any(argument => argument.StartsWith("--output", StringComparison.Ordinal))).ShouldBeFalse();
        launches.Single(launch => launch.Arguments[0] == "fetch").Arguments.ShouldBeSequence("fetch", "--all", "--tags", "--prune");

        Snapshot(clone).ShouldBe(before);
    }

    [Fact]
    public async Task WhateverIsAskedOfTheServiceNoForbiddenCommandIsStarted()
    {
        var spy = new SpyLauncher();
        var service = TestGit.Service(spy);
        var repo = NewRepo();
        var first = repo.Commit("a.txt", "one", "first");

        await service.ResolveAsync(repo.Path, "-c core.hooksPath=/tmp/x");
        await service.ResolveAsync(repo.Path, "--upload-pack=/tmp/x");
        await service.PinAsync(repo.Path, new Revision("x", "--evil", null));
        await service.PinAsync(repo.Path, new Revision("x", "main", "--output=/tmp/x"));
        await Should.ThrowAsync<GitException>(() => service.AddWorktreeAsync(repo.Path, new WorktreeRequest(Folder.Combine("w"), "../escape", first, "run1")));
        await Should.ThrowAsync<GitException>(() => service.RemoveWorktreeAsync(repo.Path, Folder.Combine("w"), repo.Path));

        spy.Launches.ShouldBeEmpty();
    }
}
