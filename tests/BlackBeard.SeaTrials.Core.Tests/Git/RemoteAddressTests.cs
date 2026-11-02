using BlackBeard.SeaTrials.Core.Git;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class RemoteAddressTests
{
    [Fact]
    public void TheSameRepositoryIsTheSameWhicheverWayItIsWritten()
    {
        var spellings = new[]
        {
            "https://example.com/acme/sample-repo.git",
            "https://example.com/acme/sample-repo",
            "https://example.com/acme/sample-repo/",
            "https://example.com/acme/sample-repo.git/",
            "HTTPS://EXAMPLE.COM/acme/sample-repo.git",
            "https://someone@example.com/acme/sample-repo.git",
            "https://someone:secret-word@example.com/acme/sample-repo.git",
            "ssh://git@example.com/acme/sample-repo.git",
            "git@example.com:acme/sample-repo.git",
            "git@example.com:acme/sample-repo",
            "example.com:acme/sample-repo.git",
            "  https://example.com/acme/sample-repo.git  ",
        };

        var first = RemoteAddress.Normalize(spellings[0]);
        first.ShouldBe("example.com/acme/sample-repo");
        foreach (var spelling in spellings)
        {
            RemoteAddress.Normalize(spelling).ShouldBe(first, spelling);
            RemoteAddress.AreSame(spelling, spellings[0]).ShouldBeTrue(spelling);
        }
    }

    [Fact]
    public void AnotherRepositoryAnotherOwnerAnotherHostOrAnotherCaseIsDifferent()
    {
        var address = "https://example.com/acme/sample-repo.git";

        RemoteAddress.AreSame(address, "https://example.com/acme/other-repo.git").ShouldBeFalse();
        RemoteAddress.AreSame(address, "https://example.com/other/sample-repo.git").ShouldBeFalse();
        RemoteAddress.AreSame(address, "https://example.org/acme/sample-repo.git").ShouldBeFalse();
        RemoteAddress.AreSame(address, "https://example.com/Acme/sample-repo.git").ShouldBeFalse();
        RemoteAddress.AreSame(address, "https://example.com/acme/sample-repo-fork.git").ShouldBeFalse();
    }

    [Fact]
    public void LocalPathsMatchWithAnySeparatorOrTrailingSlash()
    {
        RemoteAddress.AreSame("/srv/repos/sample.git", "/srv/repos/sample").ShouldBeTrue();
        RemoteAddress.AreSame("/srv/repos/sample/", "/srv/repos/sample").ShouldBeTrue();
        RemoteAddress.AreSame("file:///srv/repos/sample.git", "/srv/repos/sample").ShouldBeTrue();
        RemoteAddress.AreSame("C:\\repos\\sample", "C:/repos/sample").ShouldBeTrue();
        RemoteAddress.AreSame("file:///C:/repos/sample", "C:\\repos\\sample").ShouldBeTrue();
        RemoteAddress.AreSame("/srv/repos/sample", "/srv/repos/other").ShouldBeFalse();
    }

    [Fact]
    public void AWindowsDriveIsNotMistakenForAHost()
    {
        RemoteAddress.Normalize("C:\\repos\\sample").ShouldStartWith("file:");
        RemoteAddress.Normalize("C:/repos/sample").ShouldStartWith("file:");
    }

    [Fact]
    public void ThePortIsPartOfTheHost()
    {
        RemoteAddress.AreSame("ssh://git@example.com:2222/acme/x.git", "ssh://git@example.com/acme/x.git").ShouldBeFalse();
        RemoteAddress.AreSame("ssh://git@example.com:2222/acme/x.git", "ssh://other@example.com:2222/acme/x").ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://example.com/acme/sample-repo.git", "sample-repo")]
    [InlineData("git@example.com:acme/Sample.Repo", "Sample.Repo")]
    [InlineData("/srv/repos/sample.git", "sample")]
    [InlineData("C:\\repos\\my sample", "my_sample")]
    public void TheRepositoryNameIsAFolderNameSafeToUse(string address, string expected)
    {
        RemoteAddress.RepositoryName(address).ShouldBe(expected);
    }

    [Fact]
    public void AnAddressWithNoNameStillGivesAName()
    {
        RemoteAddress.RepositoryName("https://example.com/").ShouldNotBeNullOrEmpty();
    }
}
