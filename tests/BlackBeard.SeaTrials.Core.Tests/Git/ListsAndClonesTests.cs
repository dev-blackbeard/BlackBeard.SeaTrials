using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class ListTests : GitTestBase
{
    [Fact]
    public async Task BranchesRemoteBranchesAndTagsAreListedInThatOrderByName()
    {
        var (_, clone) = NewRemoteAndClone();
        clone.Run("branch", "topic");
        clone.Run("tag", "v1");
        clone.Run("tag", "-a", "v2", "-m", "annotated");

        var refs = await Service.ListRefsAsync(clone.Path);

        refs.Select(entry => entry.Kind + ":" + entry.Name).ShouldBeSequence(
            "Branch:main", "Branch:topic", "RemoteBranch:origin/main", "Tag:v1", "Tag:v2");
        refs.All(entry => entry.Sha == clone.Sha()).ShouldBeTrue();
    }

    [Fact]
    public async Task AnAnnotatedTagPointsAtTheCommitNotAtTheTagObject()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.Run("tag", "-a", "v1", "-m", "annotated");
        var second = repo.Commit("a.txt", "two", "second");
        repo.Run("tag", "light", second);

        var refs = await Service.ListRefsAsync(repo.Path);

        refs.Single(entry => entry.Name == "v1").Sha.ShouldBe(repo.Sha("v1^{commit}"));
        refs.Single(entry => entry.Name == "v1").Sha.ShouldNotBe(repo.Sha("v1"));
        refs.Single(entry => entry.Name == "light").Sha.ShouldBe(second);
    }

    [Fact]
    public async Task ATagThatDoesNotPointAtACommitIsLeftOut()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.Run("tag", "tree-tag", "HEAD^{tree}");

        var refs = await Service.ListRefsAsync(repo.Path);

        refs.Select(entry => entry.Name).ShouldBeSequence("main");
    }

    [Fact]
    public async Task NamesWithSlashesAreKeptWhole()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.Run("branch", "feature/a/b");

        var refs = await Service.ListRefsAsync(repo.Path);

        refs.Select(entry => entry.Name).ShouldBeSequence("feature/a/b", "main");
    }

    [Fact]
    public async Task TheNewestCommitsOnAnyBranchAreListedNewestFirstUpToTheCount()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.ImportCommits("main2", 120);
        var tip = repo.Sha("refs/heads/main2");

        var fifty = await Service.ListRecentCommitsAsync(repo.Path);
        var five = await Service.ListRecentCommitsAsync(repo.Path, 5);

        fifty.Count.ShouldBe(50);
        fifty[0].Sha.ShouldBe(tip);
        fifty[0].Subject.ShouldBe("commit 120");
        fifty[0].AuthorName.ShouldBe("Sample Author");
        fifty[1].Subject.ShouldBe("commit 119");
        five.Count.ShouldBe(5);
        five.Select(commit => commit.Sha).ShouldBeSequence(fifty.Take(5).Select(commit => commit.Sha).ToArray());
        fifty.Select(commit => commit.AuthorDate).ShouldBeSequence(fifty.Select(commit => commit.AuthorDate).OrderByDescending(date => date).ToArray());
    }

    [Fact]
    public async Task ARepositoryWithFewerCommitsListsThemAll()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        repo.Commit("a.txt", "two", "second");

        var commits = await Service.ListRecentCommitsAsync(repo.Path);

        commits.Select(commit => commit.Subject).ShouldBeSequence("second", "first");
    }

    [Fact]
    public async Task ARepositoryWithoutCommitsListsNothing()
    {
        var repo = NewRepo();

        (await Service.ListRecentCommitsAsync(repo.Path)).ShouldBeEmpty();
        (await Service.ListRefsAsync(repo.Path)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheCommitsBehindTheUsersStashAreNotListed()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");
        File.WriteAllText(Path.Combine(repo.Path, "a.txt"), "changed but not committed");
        repo.Run("stash", "-q");
        var stash = repo.Sha("refs/stash");

        var commits = await Service.ListRecentCommitsAsync(repo.Path);

        commits.Select(commit => commit.Subject).ShouldBeSequence("first");
        commits.Any(commit => commit.Sha == stash).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task ACountOutOfRangeIsRefused(int count)
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "one", "first");

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Service.ListRecentCommitsAsync(repo.Path, count));
    }
}

public class CloneTests : GitTestBase
{
    [Fact]
    public async Task ACloneIsFoundFromItsTopOrFromAFolderInsideIt()
    {
        var repo = NewRepo("project");
        repo.Commit("sub/a.txt", "one", "first");

        var fromTop = (await Service.InspectCloneAsync(repo.Path)).ShouldNotBeNull();
        var fromInside = (await Service.InspectCloneAsync(Path.Combine(repo.Path, "sub"))).ShouldNotBeNull();

        File.Exists(Path.Combine(fromTop.TopLevel, "sub", "a.txt")).ShouldBeTrue();
        fromInside.TopLevel.ShouldBe(fromTop.TopLevel);
        fromTop.Origin.ShouldBeNull();
    }

    [Fact]
    public async Task TheAddressOfOriginIsReported()
    {
        var repo = NewRepo("project");
        repo.Commit("a.txt", "one", "first");
        repo.Run("remote", "add", "origin", "https://example.com/acme/sample-repo.git");

        var clone = await Service.InspectCloneAsync(repo.Path);

        clone.ShouldNotBeNull().Origin.ShouldBe("https://example.com/acme/sample-repo.git");
    }

    [Fact]
    public async Task AFolderThatIsNotACloneIsNull()
    {
        Directory.CreateDirectory(Folder.Combine("plain"));
        var bare = TestRepo.CreateBare(Folder.Combine("bare.git"));

        (await Service.InspectCloneAsync(Folder.Combine("plain"))).ShouldBeNull();
        (await Service.InspectCloneAsync(Folder.Combine("missing"))).ShouldBeNull();
        (await Service.InspectCloneAsync(bare.Path)).ShouldBeNull();
    }

    [Fact]
    public async Task TheKnownCloneWithThatRemoteIsFoundHoweverTheRemoteIsWritten()
    {
        var alpha = NewRepo("alpha");
        alpha.Commit("a.txt", "one", "first");
        alpha.Run("remote", "add", "origin", "https://example.com/acme/alpha.git");
        var beta = NewRepo("beta");
        beta.Commit("a.txt", "one", "first");
        beta.Run("remote", "add", "origin", "git@example.com:acme/beta.git");
        var noOrigin = NewRepo("no-origin");
        noOrigin.Commit("a.txt", "one", "first");
        var candidates = new[] { Folder.Combine("missing"), Folder.Combine("no-origin"), alpha.Path, beta.Path };

        var foundBeta = (await Service.FindCloneAsync(candidates, "https://example.com/acme/beta")).ShouldNotBeNull();
        var foundAlpha = (await Service.FindCloneAsync(candidates, "ssh://git@example.com/acme/alpha.git/")).ShouldNotBeNull();

        File.Exists(Path.Combine(foundBeta.TopLevel, "a.txt")).ShouldBeTrue();
        foundBeta.TopLevel.ShouldNotBe(foundAlpha.TopLevel);
        foundBeta.Origin.ShouldBe("git@example.com:acme/beta.git");
    }

    [Fact]
    public async Task ARemoteNoKnownCloneHasIsNull()
    {
        var alpha = NewRepo("alpha");
        alpha.Commit("a.txt", "one", "first");
        alpha.Run("remote", "add", "origin", "https://example.com/acme/alpha.git");

        (await Service.FindCloneAsync(new[] { alpha.Path }, "https://example.com/acme/other.git")).ShouldBeNull();
        (await Service.FindCloneAsync(Array.Empty<string>(), "https://example.com/acme/alpha.git")).ShouldBeNull();
    }
}
