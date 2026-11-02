using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

/// <summary>SPEC 5.4: the git commands the harness may run are a closed list, pinned here.</summary>
public class GitAllowListTests
{
    private static readonly string Sha = new('a', 40);

    private static string[] Args(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void TheListIsTheOneInTheSpecification()
    {
        GitAllowList.Commands.ShouldBeSequence(
            "--version", "rev-parse", "log", "for-each-ref", "show", "status", "fetch",
            "worktree add", "worktree remove", "worktree prune", "worktree list",
            "cat-file", "submodule", "remote get-url");
    }

    [Fact]
    public void EveryCommandOnTheListIsAllowed()
    {
        var allowed = new[]
        {
            "--version",
            "rev-parse --show-toplevel",
            "rev-parse --verify abc^{commit}",
            "rev-parse HEAD",
            "log -1 --format=%H abc",
            "log --branches --remotes --tags -n 50 --format=%H",
            "for-each-ref --format=%(refname) refs/heads",
            "for-each-ref --contains abc refs/heads",
            "show -s --format=%H abc",
            "status --porcelain",
            "fetch --all --tags --prune",
            "fetch --all",
            "fetch",
            "worktree add --detach /work/repo/abc1234-run1 " + Sha,
            "worktree remove --force /work/repo/abc1234-run1",
            "worktree prune",
            "worktree list",
            "worktree list --porcelain",
            "cat-file -e abc^{commit}",
            "cat-file -t abc",
            "remote get-url origin",
        };

        foreach (var command in allowed)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeTrue(command);
        }
    }

    [Fact]
    public void WhatTheSpecificationForbidsAgainstTheUsersCopyIsRefused()
    {
        var forbidden = new[]
        {
            "checkout main", "checkout -- .", "reset --hard", "reset --soft HEAD~1", "clean -fdx", "clean -n",
            "stash", "stash pop", "pull", "pull --rebase", "merge main", "rebase main",
        };

        foreach (var command in forbidden)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeFalse(command);
            GitAllowList.IsAllowed(Args(command), GitScope.OwnWorktree).ShouldBeFalse(command + " (own worktree)");
        }
    }

    [Fact]
    public void NothingElseThatChangesARepositoryIsAllowed()
    {
        var forbidden = new[]
        {
            "commit -m x", "add .", "rm file", "mv a b", "push", "push origin main", "branch -D x", "branch x", "tag x",
            "tag -d x", "config user.name x", "restore .", "switch main", "gc", "init", "clone x y", "apply patch",
            "am patch", "cherry-pick abc", "revert abc", "update-ref -d refs/heads/x", "symbolic-ref HEAD refs/heads/x",
            "reflog expire --all", "filter-branch", "gc --prune=now", "remote add x y", "remote remove origin",
            "remote set-url origin y", "remote rename origin x", "remote", "remote -v", "worktree move a b",
            "worktree lock a", "worktree unlock a", "worktree repair", "worktree", "worktree add /x", "submodule add x y",
            "submodule init", "sparse-checkout set x", "notes add", "bisect start", "format-patch HEAD~1", "archive HEAD",
        };

        foreach (var command in forbidden)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeFalse(command);
        }
    }

    [Fact]
    public void ARemoteOrARefspecCannotBeFetchedAndNothingCanBeForced()
    {
        var forbidden = new[]
        {
            "fetch origin", "fetch origin main", "fetch origin +refs/heads/*:refs/heads/*", "fetch --all --force",
            "fetch --all --tags --prune --force", "fetch -f", "fetch --depth=1", "fetch --upload-pack=x",
        };

        foreach (var command in forbidden)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeFalse(command);
        }
    }

    [Fact]
    public void WorktreeCommandsHaveExactlyTheFormsTheHarnessUses()
    {
        var forbidden = new[]
        {
            "worktree add /work/x " + Sha,
            "worktree add --detach /work/x main",
            "worktree add --detach /work/x abc1234",
            "worktree add --detach /work/x " + Sha + " --force",
            "worktree add -f --detach /work/x " + Sha,
            "worktree add --detach -b x /work/x " + Sha,
            "worktree add --detach --lock /work/x " + Sha,
            "worktree remove /work/x",
            "worktree remove --force --force /work/x",
            "worktree remove --force -- /work/x",
            "worktree prune --expire=now",
            "worktree list --verbose",
        };

        foreach (var command in forbidden)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeFalse(command);
        }
    }

    [Fact]
    public void SubmodulesAreAllowedOnlyInAWorktreeTheHarnessMade()
    {
        var update = Args("submodule update --init --recursive");

        GitAllowList.IsAllowed(update, GitScope.OwnWorktree).ShouldBeTrue();
        GitAllowList.IsAllowed(update, GitScope.UserClone).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("submodule update --init"), GitScope.OwnWorktree).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("submodule foreach git clean -fdx"), GitScope.OwnWorktree).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("submodule update --init --recursive --force"), GitScope.OwnWorktree).ShouldBeFalse();
    }

    [Fact]
    public void ARemoteIsOnlyEverAskedForItsAddress()
    {
        GitAllowList.IsAllowed(Args("remote get-url origin"), GitScope.UserClone).ShouldBeTrue();
        GitAllowList.IsAllowed(Args("remote get-url --push origin"), GitScope.UserClone).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("remote get-url"), GitScope.UserClone).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("remote get-url -- origin"), GitScope.UserClone).ShouldBeFalse();
    }

    [Fact]
    public void NoCommandMayWriteToAFileOfItsChoosing()
    {
        GitAllowList.IsAllowed(Args("log --output=/tmp/x -1"), GitScope.UserClone).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("show --output /tmp/x abc"), GitScope.UserClone).ShouldBeFalse();
        GitAllowList.IsAllowed(Args("rev-parse --output=x HEAD"), GitScope.UserClone).ShouldBeFalse();
    }

    [Fact]
    public void AGlobalOptionIsNotACommand()
    {
        var forbidden = new[]
        {
            "-c core.hooksPath=/x status", "-c core.sshCommand=x fetch --all", "-C /somewhere status",
            "--git-dir=/x status", "--work-tree=/x status", "--exec-path=/x", "--no-pager log", "--help", "-h", "",
        };

        foreach (var command in forbidden)
        {
            GitAllowList.IsAllowed(Args(command), GitScope.UserClone).ShouldBeFalse(command);
        }

        GitAllowList.IsAllowed(Args("--version --build-options"), GitScope.UserClone).ShouldBeFalse();
    }

    [Fact]
    public void RefusingAnEmptyCommandSaysSo()
    {
        var error = Should.Throw<GitCommandNotAllowedException>(() => GitAllowList.EnsureAllowed(Array.Empty<string>(), GitScope.UserClone));

        error.Reason.ShouldContain("no command");
    }

    [Fact]
    public void RefusalNamesTheCommandAndWhy()
    {
        var error = Should.Throw<GitCommandNotAllowedException>(() => GitAllowList.EnsureAllowed(Args("reset --hard"), GitScope.UserClone));

        error.Message.ShouldContain("git reset --hard");
        error.Message.ShouldContain("not one of the git commands");
        error.Arguments.ShouldBeSequence("reset", "--hard");
    }
}
