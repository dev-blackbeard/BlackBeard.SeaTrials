using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Requests;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class ResolveTests : GitTestBase
{
    private (TestRepo Repo, string First, string Second) TwoCommits()
    {
        var repo = NewRepo();
        var first = repo.Commit("a.txt", "one", "first commit");
        var second = repo.Commit("a.txt", "two", "second commit");
        return (repo, first, second);
    }

    [Fact]
    public async Task AFullIdIsFoundWithItsDetails()
    {
        var (repo, first, second) = TwoCommits();

        var resolution = await Service.ResolveAsync(repo.Path, first);

        resolution.Status.ShouldBe(ResolutionStatus.Found);
        var commit = resolution.Commit.ShouldNotBeNull();
        commit.Sha.ShouldBe(first);
        commit.Subject.ShouldBe("first commit");
        commit.AuthorName.ShouldBe("Sample Author");
        commit.AuthorDate.ToUnixTimeSeconds().ShouldBe(long.Parse(repo.Run("log", "-1", "--format=%at", first).Trim()));
        commit.Branches.ShouldBeSequence("main");
        (await Service.ResolveAsync(repo.Path, second)).Commit.ShouldNotBeNull().Subject.ShouldBe("second commit");
    }

    [Fact]
    public async Task AnAbbreviatedIdIsExpandedToTheFullId()
    {
        var (repo, first, _) = TwoCommits();

        var resolution = await Service.ResolveAsync(repo.Path, first.Substring(0, 10));

        resolution.Status.ShouldBe(ResolutionStatus.Found);
        resolution.Commit.ShouldNotBeNull().Sha.ShouldBe(first);
    }

    [Fact]
    public async Task BranchesTagsAndAnnotatedTagsAreFound()
    {
        var (repo, first, second) = TwoCommits();
        repo.Run("branch", "topic", first);
        repo.Run("tag", "v1", first);
        repo.Run("tag", "-a", "v2", "-m", "annotated", second);

        (await Service.ResolveAsync(repo.Path, "main")).Commit.ShouldNotBeNull().Sha.ShouldBe(second);
        (await Service.ResolveAsync(repo.Path, "topic")).Commit.ShouldNotBeNull().Sha.ShouldBe(first);
        (await Service.ResolveAsync(repo.Path, "v1")).Commit.ShouldNotBeNull().Sha.ShouldBe(first);
        (await Service.ResolveAsync(repo.Path, "v2")).Commit.ShouldNotBeNull().Sha.ShouldBe(second);
        (await Service.ResolveAsync(repo.Path, "HEAD")).Commit.ShouldNotBeNull().Sha.ShouldBe(second);
    }

    [Fact]
    public async Task ExpressionsGitUnderstandsAreResolved()
    {
        var (repo, first, second) = TwoCommits();

        (await Service.ResolveAsync(repo.Path, second + "^")).Commit.ShouldNotBeNull().Sha.ShouldBe(first);
        (await Service.ResolveAsync(repo.Path, "main^")).Commit.ShouldNotBeNull().Sha.ShouldBe(first);
        (await Service.ResolveAsync(repo.Path, "main~1")).Commit.ShouldNotBeNull().Sha.ShouldBe(first);
        (await Service.ResolveAsync(repo.Path, "HEAD~0")).Commit.ShouldNotBeNull().Sha.ShouldBe(second);
    }

    [Fact]
    public async Task TheBranchesThatContainTheCommitAreListed()
    {
        var (repo, first, second) = TwoCommits();
        repo.Run("branch", "topic", first);

        var oldCommit = (await Service.ResolveAsync(repo.Path, first)).Commit.ShouldNotBeNull();
        var newCommit = (await Service.ResolveAsync(repo.Path, second)).Commit.ShouldNotBeNull();

        oldCommit.Branches.ShouldBeSequence("main", "topic");
        newCommit.Branches.ShouldBeSequence("main");
    }

    [Fact]
    public async Task AnUnknownNameOrIdIsNotFoundAndNotGuessed()
    {
        var (repo, _, _) = TwoCommits();

        var name = await Service.ResolveAsync(repo.Path, "no-such-branch");
        var id = await Service.ResolveAsync(repo.Path, new string('1', 40));

        name.Status.ShouldBe(ResolutionStatus.NotFound);
        name.Commit.ShouldBeNull();
        name.Message.ShouldNotBeNull().ShouldContain("no-such-branch");
        id.Status.ShouldBe(ResolutionStatus.NotFound);
    }

    [Fact]
    public async Task AnIdOfSomethingThatIsNotACommitIsNotFound()
    {
        var (repo, _, _) = TwoCommits();
        var blob = repo.Run("rev-parse", "HEAD:a.txt").Trim();

        (await Service.ResolveAsync(repo.Path, blob)).Status.ShouldBe(ResolutionStatus.NotFound);
    }

    [Fact]
    public async Task AnAbbreviatedIdThatMatchesTwoCommitsIsAmbiguousNotAGuess()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.ImportCommits("many", 1500); // 1500 ids: two of them share their first four digits, as good as certainly
        var ids = repo.Run("rev-list", "refs/heads/many").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).ToList();
        var prefix = ids.GroupBy(id => id.Substring(0, 4)).First(group => group.Count() > 1).Key;

        var resolution = await Service.ResolveAsync(repo.Path, prefix);

        resolution.Status.ShouldBe(ResolutionStatus.Ambiguous);
        resolution.Commit.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-x")]
    [InlineData("--upload-pack=anything")]
    [InlineData("--output=/tmp/anything")]
    public async Task ARevisionThatCouldBeTakenForAnOptionIsInvalid(string rev)
    {
        var (repo, _, _) = TwoCommits();

        var resolution = await Service.ResolveAsync(repo.Path, rev);

        resolution.Status.ShouldBe(ResolutionStatus.Invalid);
    }

    [Fact]
    public async Task ARevisionWithAControlCharacterIsInvalid()
    {
        var (repo, _, _) = TwoCommits();

        (await Service.ResolveAsync(repo.Path, "main\n--evil")).Status.ShouldBe(ResolutionStatus.Invalid);
    }
}

public class PinTests : GitTestBase
{
    private (TestRepo Repo, string First, string Second) TwoCommits()
    {
        var repo = NewRepo();
        var first = repo.Commit("a.txt", "one", "first commit");
        var second = repo.Commit("a.txt", "two", "second commit");
        return (repo, first, second);
    }

    [Fact]
    public async Task TheRequestedShaRunsAndARevThatAgreesIsQuiet()
    {
        var (repo, first, second) = TwoCommits();

        var after = await Service.PinAsync(repo.Path, new Revision("after", "main", second));
        var before = await Service.PinAsync(repo.Path, new Revision("before", "main^", first));
        var byExpression = await Service.PinAsync(repo.Path, new Revision("before again", second + "^", first));

        foreach (var result in new[] { after, before, byExpression })
        {
            result.Outcome.ShouldBe(PinOutcome.Pinned);
            result.CanRun.ShouldBeTrue();
            result.Anomalies.ShouldBeEmpty();
            result.Failure.ShouldBe(PinFailure.None);
        }

        after.Sha.ShouldBe(second);
        after.Commit.ShouldNotBeNull().Subject.ShouldBe("second commit");
        before.Sha.ShouldBe(first);
    }

    [Fact]
    public async Task ABranchThatMovedSinceTheRequestIsAnAnomalyAndTheRequestedCommitStillRuns()
    {
        var (repo, first, second) = TwoCommits();
        repo.Run("branch", "topic", second);
        var third = repo.Commit("a.txt", "three", "third commit");
        repo.Run("branch", "-f", "topic", third); // the branch moved on after the request named `second`

        var result = await Service.PinAsync(repo.Path, new Revision("topic", "topic", second));

        result.Outcome.ShouldBe(PinOutcome.Pinned);
        result.CanRun.ShouldBeTrue();
        result.Sha.ShouldBe(second);
        var anomaly = result.Anomalies.ShouldHaveSingleItem();
        anomaly.Kind.ShouldBe(PinAnomalyKind.RevMovedSinceRequest);
        anomaly.RevResolvesTo.ShouldBe(third);
        anomaly.Message.ShouldContain(second);
        anomaly.Message.ShouldContain(third);
        first.ShouldNotBe(second);
    }

    [Fact]
    public async Task ARevThatNoLongerResolvesIsAnAnomalyAndTheShaStillRuns()
    {
        var (repo, first, _) = TwoCommits();

        var result = await Service.PinAsync(repo.Path, new Revision("gone", "deleted-branch", first));

        result.Outcome.ShouldBe(PinOutcome.Pinned);
        result.Sha.ShouldBe(first);
        result.Anomalies.ShouldHaveSingleItem().Kind.ShouldBe(PinAnomalyKind.RevNoLongerResolves);
    }

    [Fact]
    public async Task AnUnknownCommitFailsThatRevisionAloneAndSaysWhy()
    {
        var (repo, _, second) = TwoCommits();
        var unknown = new string('a', 40);

        var failed = await Service.PinAsync(repo.Path, new Revision("not pushed", "main", unknown));
        var fine = await Service.PinAsync(repo.Path, new Revision("pushed", "main", second));

        failed.Outcome.ShouldBe(PinOutcome.Failed);
        failed.CanRun.ShouldBeFalse();
        failed.Failure.ShouldBe(PinFailure.CommitMissing);
        failed.Sha.ShouldBeNull();
        failed.Commit.ShouldBeNull();
        var message = failed.Message.ShouldNotBeNull();
        message.ShouldContain(unknown);
        message.ShouldContain("fetched");
        fine.CanRun.ShouldBeTrue();
    }

    [Fact]
    public async Task AShaThatIsTheIdOfATreeIsNotACommit()
    {
        var (repo, _, _) = TwoCommits();
        var tree = repo.Sha("HEAD^{tree}");

        var result = await Service.PinAsync(repo.Path, new Revision("tree", null, tree));

        result.Failure.ShouldBe(PinFailure.CommitMissing);
    }

    [Fact]
    public async Task AShaThatIsNotAFullLowerCaseIdIsRefused()
    {
        var (repo, _, _) = TwoCommits();

        foreach (var sha in new[] { "abc1234", "main", new string('A', 40), new string('a', 39), new string('a', 41) })
        {
            var result = await Service.PinAsync(repo.Path, new Revision("x", "main", sha));

            result.Failure.ShouldBe(PinFailure.InvalidSha, sha);
            result.CanRun.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task WithoutAShaTheRevIsPinnedOnOpening()
    {
        var (repo, first, second) = TwoCommits();

        var branch = await Service.PinAsync(repo.Path, new Revision("tip", "main", null));
        var expression = await Service.PinAsync(repo.Path, new Revision("parent", "main^", null));

        branch.Outcome.ShouldBe(PinOutcome.PinnedOnOpening);
        branch.CanRun.ShouldBeTrue();
        branch.Sha.ShouldBe(second);
        branch.Anomalies.ShouldBeEmpty();
        branch.Message.ShouldNotBeNull().ShouldContain("Pinned on opening");
        expression.Sha.ShouldBe(first);
    }

    [Fact]
    public async Task WithoutAShaAnUnknownOrInvalidRevFails()
    {
        var (repo, _, _) = TwoCommits();

        var unknown = await Service.PinAsync(repo.Path, new Revision("x", "no-such-branch", null));
        var invalid = await Service.PinAsync(repo.Path, new Revision("x", "--evil", null));

        unknown.Failure.ShouldBe(PinFailure.RevisionNotFound);
        unknown.CanRun.ShouldBeFalse();
        invalid.Failure.ShouldBe(PinFailure.InvalidRevision);
    }

    [Fact]
    public async Task AnAmbiguousRevWithoutAShaFailsInsteadOfPickingOne()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.ImportCommits("many", 1500);
        var ids = repo.Run("rev-list", "refs/heads/many").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(id => id.Trim()).ToList();
        var prefix = ids.GroupBy(id => id.Substring(0, 4)).First(group => group.Count() > 1).Key;

        var result = await Service.PinAsync(repo.Path, new Revision("x", prefix, null));

        result.Failure.ShouldBe(PinFailure.RevisionAmbiguous);
        result.CanRun.ShouldBeFalse();
    }

    [Fact]
    public async Task ARevisionWithNeitherRevNorShaCannotBePinned()
    {
        var (repo, _, _) = TwoCommits();

        var result = await Service.PinAsync(repo.Path, new Revision("empty"));

        result.Failure.ShouldBe(PinFailure.NothingToPin);
    }
}
