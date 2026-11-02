using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Requests;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class FetchTests : GitTestBase
{
    [Fact]
    public async Task AFetchMakesACommitOnAThrowawayRemoteBranchReachableWithoutMovingTheUsersBranches()
    {
        var (remote, clone) = NewRemoteAndClone();
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        other.Run("checkout", "-q", "-b", "throwaway");
        var pushed = other.Commit("b.txt", "new", "on a throwaway branch");
        other.Run("push", "-q", "origin", "throwaway");
        var localBranchesBefore = clone.Run("for-each-ref", "refs/heads");
        var headBefore = clone.Sha();

        (await Service.ResolveAsync(clone.Path, pushed)).Status.ShouldBe(ResolutionStatus.NotFound);
        await Service.FetchAsync(clone.Path);
        var after = await Service.ResolveAsync(clone.Path, pushed);

        after.Status.ShouldBe(ResolutionStatus.Found);
        after.Commit.ShouldNotBeNull().Branches.ShouldBeSequence("origin/throwaway");
        (await Service.ListRefsAsync(clone.Path)).Any(entry => entry.Name == "origin/throwaway").ShouldBeTrue();
        clone.Run("for-each-ref", "refs/heads").ShouldBe(localBranchesBefore);
        clone.Sha().ShouldBe(headBefore);
        clone.Run("rev-parse", "--abbrev-ref", "HEAD").Trim().ShouldBe("main");
    }

    [Fact]
    public async Task AFetchPrunesBranchesThatAreGoneFromTheRemote()
    {
        var (remote, clone) = NewRemoteAndClone();
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        other.Run("checkout", "-q", "-b", "short-lived");
        other.Commit("b.txt", "new", "short lived");
        other.Run("push", "-q", "origin", "short-lived");
        await Service.FetchAsync(clone.Path);
        (await Service.ListRefsAsync(clone.Path)).Any(entry => entry.Name == "origin/short-lived").ShouldBeTrue();

        other.Run("push", "-q", "origin", "--delete", "short-lived");
        await Service.FetchAsync(clone.Path);

        (await Service.ListRefsAsync(clone.Path)).Any(entry => entry.Name == "origin/short-lived").ShouldBeFalse();
    }

    [Fact]
    public async Task AFetchFetchesTags()
    {
        var (remote, clone) = NewRemoteAndClone();
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        other.Run("tag", "v1");
        other.Run("push", "-q", "origin", "v1");

        await Service.FetchAsync(clone.Path);

        (await Service.ListRefsAsync(clone.Path)).Any(entry => entry.Kind == RefKind.Tag && entry.Name == "v1").ShouldBeTrue();
    }

    [Fact]
    public async Task AFetchLeavesTheUsersOwnLocalTagsAlone()
    {
        var (_, clone) = NewRemoteAndClone();
        clone.Run("tag", "only-on-my-pc");

        await Service.FetchAsync(clone.Path);

        (await Service.ListRefsAsync(clone.Path)).Any(entry => entry.Name == "only-on-my-pc").ShouldBeTrue();
    }

    [Fact]
    public async Task AFetchInARepositoryWithoutRemotesDoesNothing()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");

        await Service.FetchAsync(repo.Path);

        repo.Sha().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task AFetchFromARemoteThatIsGoneFailsWithGitsMessage()
    {
        var (remote, clone) = NewRemoteAndClone();
        TempFolder.ForceDelete(remote.Path);

        var error = await Should.ThrowAsync<GitException>(() => Service.FetchAsync(clone.Path));

        error.Kind.ShouldBe(GitFailureKind.CommandFailed);
        error.Message.ShouldContain("fetch");
        error.Output.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ABranchThatMovedAfterTheRequestIsFoundOnceTheNewStateIsFetched()
    {
        var (remote, clone) = NewRemoteAndClone();
        var requested = clone.Sha();
        var revision = new Revision("after", "origin/main", requested);
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        var moved = other.Commit("b.txt", "newer", "somebody pushed more");
        other.Run("push", "-q", "origin", "main");

        var before = await Service.PinAsync(clone.Path, revision);
        await Service.FetchAsync(clone.Path);
        var after = await Service.PinAsync(clone.Path, revision);

        before.Anomalies.ShouldBeEmpty();
        after.Outcome.ShouldBe(PinOutcome.Pinned);
        after.Sha.ShouldBe(requested);
        var anomaly = after.Anomalies.ShouldHaveSingleItem();
        anomaly.Kind.ShouldBe(PinAnomalyKind.RevMovedSinceRequest);
        anomaly.RevResolvesTo.ShouldBe(moved);
    }

    [Fact]
    public async Task ACommitThatWasNeverFetchedIsMissingUntilItIs()
    {
        var (remote, clone) = NewRemoteAndClone();
        var other = TestRepo.Clone(remote.Path, Folder.Combine("other"));
        var pushed = other.Commit("b.txt", "newer", "pushed after the clone");
        other.Run("push", "-q", "origin", "main");
        var revision = new Revision("pushed", "origin/main", pushed);

        var before = await Service.PinAsync(clone.Path, revision);
        await Service.FetchAsync(clone.Path);
        var after = await Service.PinAsync(clone.Path, revision);

        before.Failure.ShouldBe(PinFailure.CommitMissing);
        after.Outcome.ShouldBe(PinOutcome.Pinned);
        after.Anomalies.ShouldBeEmpty();
    }
}
