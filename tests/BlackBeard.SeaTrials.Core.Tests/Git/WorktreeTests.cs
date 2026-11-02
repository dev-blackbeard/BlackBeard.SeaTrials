using System.Security.Cryptography;
using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class WorktreeTests : GitTestBase
{
    private (TestRepo Repo, string First, string Second, string WorkRoot) Setup()
    {
        var repo = NewRepo("project");
        var first = repo.Commit("src/a.txt", "one", "first");
        var second = repo.Commit("src/a.txt", "two", "second");
        return (repo, first, second, Folder.Combine("work"));
    }

    private static WorktreeRequest Request(string workRoot, string sha, string run = "run1", bool submodules = true, int warnAt = 200) =>
        new(workRoot, "SampleRepo", sha, run, submodules, warnAt);

    // Everything about the user's checkout that a harness must not change.
    private static string Snapshot(TestRepo repo)
    {
        var index = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index"))));
        return string.Join(
            "\n--\n",
            repo.Run("rev-parse", "HEAD"),
            repo.Run("rev-parse", "--abbrev-ref", "HEAD"),
            repo.Run("status", "--porcelain"),
            repo.Run("for-each-ref", "refs/heads"),
            repo.Run("reflog"),
            index,
            File.ReadAllText(Path.Combine(repo.Path, "src", "a.txt")),
            File.ReadAllText(Path.Combine(repo.Path, "untracked.txt")));
    }

    [Fact]
    public async Task AWorktreeIsMadeAtThePinnedCommitInTheDocumentedPlace()
    {
        var (repo, first, _, workRoot) = Setup();

        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));

        var expected = Path.GetFullPath(Path.Combine(workRoot, "SampleRepo", first.Substring(0, 7) + "-run1"));
        tree.Path.ShouldBe(expected);
        tree.Sha.ShouldBe(first);
        tree.Warnings.ShouldBeEmpty();
        Directory.Exists(expected).ShouldBeTrue();
        File.ReadAllText(Path.Combine(expected, "src", "a.txt")).ShouldBe("one");
        RawGit.Run(expected, "rev-parse", "HEAD").Trim().ShouldBe(first);
        RawGit.Run(expected, "rev-parse", "--abbrev-ref", "HEAD").Trim().ShouldBe("HEAD");
    }

    [Fact]
    public async Task TheCommitTheUserHasCheckedOutCanHaveAWorktreeToo()
    {
        var (repo, _, second, workRoot) = Setup();

        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, second));

        File.ReadAllText(Path.Combine(tree.Path, "src", "a.txt")).ShouldBe("two");
    }

    [Fact]
    public async Task TwoRunsOfTheSameCommitGetTheirOwnFolders()
    {
        var (repo, first, _, workRoot) = Setup();

        var one = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first, "run1"));
        var two = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first, "run2"));

        one.Path.ShouldNotBe(two.Path);
        Directory.Exists(one.Path).ShouldBeTrue();
        Directory.Exists(two.Path).ShouldBeTrue();
        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task TheUsersCheckoutIsNotTouchedByMakingUsingAndRemovingWorktrees()
    {
        var (repo, first, _, workRoot) = Setup();
        File.WriteAllText(Path.Combine(repo.Path, "src", "a.txt"), "dirty work in progress");
        File.WriteAllText(Path.Combine(repo.Path, "untracked.txt"), "not added to git");
        var before = Snapshot(repo);

        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));
        await Service.CaptureStateAsync(tree.Path);
        File.ReadAllText(Path.Combine(tree.Path, "src", "a.txt")).ShouldBe("one"); // the commit, not the user's work in progress
        await Service.RemoveWorktreeAsync(repo.Path, workRoot, tree.Path);
        await Service.CleanUpWorktreesAsync(repo.Path, workRoot);

        Snapshot(repo).ShouldBe(before);
    }

    [Theory]
    [InlineData("abc1234", "SampleRepo", "run1")]
    [InlineData("full", "a/b", "run1")]
    [InlineData("full", "..", "run1")]
    [InlineData("full", "", "run1")]
    [InlineData("full", "SampleRepo", "a\\b")]
    [InlineData("full", "SampleRepo", "..")]
    [InlineData("full", "SampleRepo", "")]
    public async Task ARequestThatCouldEscapeTheWorkRootIsRefused(string sha, string repoName, string runId)
    {
        var (repo, first, _, workRoot) = Setup();
        var request = new WorktreeRequest(workRoot, repoName, sha == "full" ? first : sha, runId);

        var error = await Should.ThrowAsync<GitException>(() => Service.AddWorktreeAsync(repo.Path, request));

        error.Kind.ShouldBe(GitFailureKind.InvalidArgument);
        Directory.Exists(workRoot).ShouldBeFalse();
    }

    [Fact]
    public async Task AWorkRootThatIsNotAFullPathIsRefused()
    {
        var (repo, first, _, _) = Setup();

        var error = await Should.ThrowAsync<GitException>(
            () => Service.AddWorktreeAsync(repo.Path, Request(Path.Combine("relative", "work"), first)));

        error.Kind.ShouldBe(GitFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task AFolderThatAlreadyExistsIsNeverReused()
    {
        var (repo, first, _, workRoot) = Setup();
        await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));

        var error = await Should.ThrowAsync<GitException>(() => Service.AddWorktreeAsync(repo.Path, Request(workRoot, first)));

        error.Kind.ShouldBe(GitFailureKind.WorktreeExists);
    }

    [Fact]
    public async Task AnUnknownCommitFailsAndLeavesNoWorktreeBehind()
    {
        var (repo, _, _, workRoot) = Setup();

        var error = await Should.ThrowAsync<GitException>(
            () => Service.AddWorktreeAsync(repo.Path, Request(workRoot, new string('a', 40))));

        error.Kind.ShouldBe(GitFailureKind.CommandFailed);
        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task RemovingAWorktreeDeletesItsFolderAndForgetsIt()
    {
        var (repo, first, _, workRoot) = Setup();
        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));
        File.WriteAllText(Path.Combine(tree.Path, "build-output.txt"), "left by a build");

        await Service.RemoveWorktreeAsync(repo.Path, workRoot, tree.Path);

        Directory.Exists(tree.Path).ShouldBeFalse();
        var remaining = await Service.ListWorktreesAsync(repo.Path);
        remaining.Count.ShouldBe(1);
        remaining[0].IsMain.ShouldBeTrue();
    }

    [Fact]
    public async Task RemovingAWorktreeWhoseFolderIsAlreadyGoneWorks()
    {
        var (repo, first, _, workRoot) = Setup();
        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));
        TempFolder.ForceDelete(tree.Path);

        await Service.RemoveWorktreeAsync(repo.Path, workRoot, tree.Path);

        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFolderGitDoesNotKnowIsClearedWhenItIsInsideTheWorkRoot()
    {
        var (repo, _, _, workRoot) = Setup();
        var leftover = Path.Combine(workRoot, "SampleRepo", "leftover");
        Directory.CreateDirectory(Path.Combine(leftover, "bin"));
        var locked = Path.Combine(leftover, "bin", "read-only.dll");
        File.WriteAllText(locked, "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly);

        await Service.RemoveWorktreeAsync(repo.Path, workRoot, leftover);

        Directory.Exists(leftover).ShouldBeFalse();
    }

    [Fact]
    public async Task NothingOutsideTheWorkRootIsEverRemoved()
    {
        var (repo, first, _, workRoot) = Setup();
        var elsewhere = Folder.Combine("elsewhere");
        repo.Run("worktree", "add", "-q", "--detach", elsewhere, first); // the user's own, made by hand
        Directory.CreateDirectory(workRoot);

        var targets = new[]
        {
            elsewhere,
            workRoot,
            Folder.Root,
            Path.Combine(workRoot, "..", "elsewhere"),
            Path.Combine(workRoot, "..", "project"),
        };

        foreach (var target in targets)
        {
            var error = await Should.ThrowAsync<GitException>(() => Service.RemoveWorktreeAsync(repo.Path, workRoot, target));
            error.Kind.ShouldBe(GitFailureKind.OutsideWorkRoot, target);
        }

        Directory.Exists(elsewhere).ShouldBeTrue();
        File.Exists(Path.Combine(repo.Path, "src", "a.txt")).ShouldBeTrue();
        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheUsersOwnCheckoutIsRefusedEvenWhenItIsInsideTheWorkRoot()
    {
        var (repo, _, _, _) = Setup();

        var error = await Should.ThrowAsync<GitException>(() => Service.RemoveWorktreeAsync(repo.Path, Folder.Root, repo.Path));

        error.Kind.ShouldBe(GitFailureKind.OutsideWorkRoot);
        error.Message.ShouldContain("own checkout");
        File.Exists(Path.Combine(repo.Path, "src", "a.txt")).ShouldBeTrue();
        Directory.Exists(Path.Combine(repo.Path, ".git")).ShouldBeTrue();
    }

    [Fact]
    public async Task PruningForgetsWorktreesWhoseFolderWasDeletedByHand()
    {
        var (repo, first, _, workRoot) = Setup();
        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));
        TempFolder.ForceDelete(tree.Path);

        await Service.PruneWorktreesAsync(repo.Path);

        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task CleanUpRemovesTheHarnessWorktreesAndLeavesTheOthers()
    {
        var (repo, first, second, workRoot) = Setup();
        await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first, "run1"));
        await Service.AddWorktreeAsync(repo.Path, Request(workRoot, second, "run2"));
        var elsewhere = Folder.Combine("elsewhere");
        repo.Run("worktree", "add", "-q", "--detach", elsewhere, first);

        var removed = await Service.CleanUpWorktreesAsync(repo.Path, workRoot);

        removed.ShouldBe(2);
        Directory.Exists(elsewhere).ShouldBeTrue();
        Directory.EnumerateDirectories(Path.Combine(workRoot, "SampleRepo")).ShouldBeEmpty();
        (await Service.ListWorktreesAsync(repo.Path)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListingTellsTheMainCheckoutFromTheOthers()
    {
        var (repo, first, second, workRoot) = Setup();
        var tree = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first));

        var entries = await Service.ListWorktreesAsync(repo.Path);

        entries.Count.ShouldBe(2);
        entries[0].IsMain.ShouldBeTrue();
        entries[0].Branch.ShouldBe("refs/heads/main");
        entries[0].HeadSha.ShouldBe(second);
        entries[0].Detached.ShouldBeFalse();
        entries[1].IsMain.ShouldBeFalse();
        entries[1].Detached.ShouldBeTrue();
        entries[1].HeadSha.ShouldBe(first);
        File.Exists(Path.Combine(entries[1].Path, "src", "a.txt")).ShouldBeTrue();
        tree.Path.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubmodulesAreInitialisedWhenTheRepositoryHasThemAndTheRequestSaysSo()
    {
        var library = NewRepo("library");
        library.Commit("lib.txt", "library file", "library commit");
        var app = NewRepo("app");
        app.Run("-c", "protocol.file.allow=always", "submodule", "add", "-q", library.Path, "lib");
        app.Run("commit", "-q", "-m", "add the submodule");
        var sha = app.Sha();
        var workRoot = Folder.Combine("work");

        var with = await Service.AddWorktreeAsync(app.Path, Request(workRoot, sha, "with"));
        var without = await Service.AddWorktreeAsync(app.Path, Request(workRoot, sha, "without", submodules: false));

        File.ReadAllText(Path.Combine(with.Path, "lib", "lib.txt")).ShouldBe("library file");
        File.Exists(Path.Combine(without.Path, "lib", "lib.txt")).ShouldBeFalse();
    }

    [Fact]
    public async Task AVeryLongPathGetsAWarningAndAShortOneDoesNot()
    {
        var (repo, first, _, workRoot) = Setup();

        var normal = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first, "run1"));
        var strict = await Service.AddWorktreeAsync(repo.Path, Request(workRoot, first, "run2", warnAt: 10));

        normal.Warnings.ShouldBeEmpty();
        var warning = strict.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("longest path");
        warning.ShouldContain("10");
    }
}

/// <summary>The stale-commit guard on real worktrees: HEAD moved under the run, files left by a build, files changed by a step.</summary>
public class StaleCommitTests : GitTestBase
{
    private async Task<(TestRepo Repo, string Sha, Worktree Tree)> NewWorktree(string ignore = "")
    {
        var repo = NewRepo("project");
        if (ignore.Length > 0)
        {
            repo.Commit(".gitignore", ignore, "ignore build output");
        }

        repo.Commit("src/a.txt", "one", "first");
        var sha = repo.Sha();
        repo.Commit("src/a.txt", "two", "second");
        var tree = await Service.AddWorktreeAsync(repo.Path, new WorktreeRequest(Folder.Combine("work"), "SampleRepo", sha, "run1"));
        return (repo, sha, tree);
    }

    [Fact]
    public async Task AFreshWorktreeIsAtThePinnedCommitAndClean()
    {
        var (_, sha, tree) = await NewWorktree();

        var state = await Service.CaptureStateAsync(tree.Path);
        var verdict = StaleGuard.CheckBeforeFirstStep(state, sha);

        state.HeadSha.ShouldBe(sha);
        state.StatusLines.ShouldBeEmpty();
        verdict.Verdict.ShouldBe(GuardVerdict.Ok);
    }

    [Fact]
    public async Task AMovedHeadIsStaleAndNothingRuns()
    {
        var (_, sha, tree) = await NewWorktree();
        RawGit.Run(tree.Path, "commit", "-q", "--allow-empty", "-m", "somebody committed in the worktree");

        var state = await Service.CaptureStateAsync(tree.Path);
        var before = StaleGuard.CheckBeforeFirstStep(state, sha);
        var after = StaleGuard.CheckAfterLastStep(state, sha);

        state.HeadSha.ShouldNotBe(sha);
        before.Verdict.ShouldBe(GuardVerdict.Stale);
        before.Message.ShouldContain(sha);
        before.Message.ShouldContain("Nothing was run");
        after.Verdict.ShouldBe(GuardVerdict.Stale);
    }

    [Fact]
    public async Task AHeadMovedByCheckingOutAnotherCommitIsStaleToo()
    {
        var (repo, sha, tree) = await NewWorktree();
        RawGit.Run(tree.Path, "checkout", "-q", "--detach", repo.Sha("main"));

        var state = await Service.CaptureStateAsync(tree.Path);

        StaleGuard.CheckBeforeFirstStep(state, sha).Verdict.ShouldBe(GuardVerdict.Stale);
    }

    [Fact]
    public async Task AFileLeftInTheWorktreeBeforeTheFirstStepMeansNotClean()
    {
        var (_, sha, tree) = await NewWorktree();
        File.WriteAllText(Path.Combine(tree.Path, "left-behind.txt"), "x");

        var state = await Service.CaptureStateAsync(tree.Path);

        state.StatusLines.ShouldBeSequence("?? left-behind.txt");
        StaleGuard.CheckBeforeFirstStep(state, sha).Verdict.ShouldBe(GuardVerdict.NotClean);
    }

    [Fact]
    public async Task IgnoredBuildOutputDoesNotCount()
    {
        var (_, sha, tree) = await NewWorktree("bin/\n");
        Directory.CreateDirectory(Path.Combine(tree.Path, "bin"));
        File.WriteAllText(Path.Combine(tree.Path, "bin", "out.dll"), "built");

        var state = await Service.CaptureStateAsync(tree.Path);

        state.StatusLines.ShouldBeEmpty();
        StaleGuard.CheckBeforeFirstStep(state, sha).Verdict.ShouldBe(GuardVerdict.Ok);
        StaleGuard.CheckAfterLastStep(state, sha).Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATrackedFileChangedByAStepIsAWarningAfterTheLastStep()
    {
        var (_, sha, tree) = await NewWorktree();
        File.WriteAllText(Path.Combine(tree.Path, "src", "a.txt"), "changed by a step");
        File.WriteAllText(Path.Combine(tree.Path, "new-output.txt"), "untracked output");

        var state = await Service.CaptureStateAsync(tree.Path);
        var after = StaleGuard.CheckAfterLastStep(state, sha);

        after.Verdict.ShouldBe(GuardVerdict.Ok);
        var warning = after.Warnings.ShouldHaveSingleItem();
        warning.ShouldContain("src/a.txt");
    }
}
