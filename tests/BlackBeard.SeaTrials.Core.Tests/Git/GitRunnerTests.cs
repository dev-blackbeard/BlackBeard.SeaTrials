using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

public class GitRunnerTests
{
    private sealed class RecordingLauncher : IGitProcessLauncher
    {
        public List<GitLaunch> Launches { get; } = new();

        public GitResult Result { get; set; } = new(0, "", "");

        public Task<GitResult> LaunchAsync(GitLaunch launch, CancellationToken cancellationToken)
        {
            Launches.Add(launch);
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task ACommandOffTheListNeverReachesTheLauncher()
    {
        var launcher = new RecordingLauncher();
        var runner = new GitRunner(null, launcher);

        foreach (var command in new[] { "checkout main", "reset --hard", "clean -fdx", "stash", "pull", "merge main" })
        {
            await Should.ThrowAsync<GitCommandNotAllowedException>(
                () => runner.RunAsync(new GitInvocation("/somewhere", command.Split(' '))));
        }

        launcher.Launches.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACommandOnTheListIsLaunchedWithItsFolderArgumentsAndScope()
    {
        var launcher = new RecordingLauncher { Result = new GitResult(3, "out", "err") };
        var runner = new GitRunner(new GitProcessOptions { GitExecutable = "my-git" }, launcher);

        var result = await runner.RunAsync(new GitInvocation("/clone", new[] { "log", "-1" }, GitScope.OwnWorktree));

        result.ExitCode.ShouldBe(3);
        result.StandardOutput.ShouldBe("out");
        result.StandardError.ShouldBe("err");
        result.Succeeded.ShouldBeFalse();
        var launch = launcher.Launches.ShouldHaveSingleItem();
        launch.Executable.ShouldBe("my-git");
        launch.WorkingDirectory.ShouldBe("/clone");
        launch.Arguments.ShouldBeSequence("log", "-1");
        launch.Scope.ShouldBe(GitScope.OwnWorktree);
    }

    [Fact]
    public async Task EveryCommandRunsWithoutPromptsLocksOrLocalisation()
    {
        var launcher = new RecordingLauncher();
        var runner = new GitRunner(null, launcher);

        await runner.RunAsync(new GitInvocation("/clone", new[] { "status", "--porcelain" }));

        var environment = launcher.Launches.ShouldHaveSingleItem().Environment;
        environment["GIT_TERMINAL_PROMPT"].ShouldBe("0");
        environment["GIT_OPTIONAL_LOCKS"].ShouldBe("0");
        environment["LC_ALL"].ShouldBe("C");
        environment["GIT_CONFIG_COUNT"].ShouldBe("4");
        var keys = Enumerable.Range(0, 4).Select(i => environment["GIT_CONFIG_KEY_" + i]).ToList();
        keys.ShouldContain("core.quotepath");
        keys.ShouldContain("gc.auto");
        environment["GIT_CONFIG_VALUE_" + keys.IndexOf("gc.auto")].ShouldBe("0");
    }

    [Fact]
    public async Task ConfigurationAndVariablesFromTheOptionsComeLastAndWin()
    {
        var launcher = new RecordingLauncher();
        var options = new GitProcessOptions
        {
            Environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "1", ["SAMPLE"] = "x" },
            Config = new[] { new KeyValuePair<string, string>("protocol.file.allow", "always") },
        };
        var runner = new GitRunner(options, launcher);

        await runner.RunAsync(new GitInvocation("/clone", new[] { "rev-parse", "HEAD" }));

        var environment = launcher.Launches.ShouldHaveSingleItem().Environment;
        environment["GIT_TERMINAL_PROMPT"].ShouldBe("1");
        environment["SAMPLE"].ShouldBe("x");
        environment["GIT_CONFIG_COUNT"].ShouldBe("5");
        environment["GIT_CONFIG_KEY_4"].ShouldBe("protocol.file.allow");
        environment["GIT_CONFIG_VALUE_4"].ShouldBe("always");
    }

    [Fact]
    public async Task RepositoryVariablesInheritedFromWhateverStartedTheHarnessAreRemoved()
    {
        var launcher = new RecordingLauncher();
        var runner = new GitRunner(null, launcher);

        await runner.RunAsync(new GitInvocation("/clone", new[] { "rev-parse", "HEAD" }));

        var removed = launcher.Launches.ShouldHaveSingleItem().RemovedEnvironment;
        removed.ShouldContain("GIT_DIR");
        removed.ShouldContain("GIT_WORK_TREE");
        removed.ShouldContain("GIT_INDEX_FILE");
        removed.ShouldContain("GIT_COMMON_DIR");
    }
}

public class SystemGitProcessLauncherTests : GitTestBase
{
    [Fact]
    public async Task RealGitAnswersWithItsVersion()
    {
        var version = await Service.GetVersionAsync();

        version.ShouldStartWith("git version");
    }

    [Fact]
    public async Task AFailureComesBackWithItsExitCodeAndMessage()
    {
        var repo = NewRepo();
        repo.Commit("a.txt", "1", "first");
        var runner = TestGit.Runner();

        var result = await runner.RunAsync(new GitInvocation(repo.Path, new[] { "rev-parse", "--verify", "no-such-thing^{commit}" }));

        result.Succeeded.ShouldBeFalse();
        result.StandardError.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AGitThatIsNotThereIsReportedAsSuch()
    {
        var runner = new GitRunner(new GitProcessOptions { GitExecutable = "no-such-git-for-the-tests" });

        var error = await Should.ThrowAsync<GitException>(
            () => runner.RunAsync(new GitInvocation(Folder.Root, new[] { "--version" })));

        error.Kind.ShouldBe(GitFailureKind.GitNotFound);
    }

    [Fact]
    public async Task AFolderThatDoesNotExistIsReportedAsSuch()
    {
        var runner = TestGit.Runner();

        var error = await Should.ThrowAsync<GitException>(
            () => runner.RunAsync(new GitInvocation(Folder.Combine("missing"), new[] { "status" })));

        error.Kind.ShouldBe(GitFailureKind.InvalidArgument);
    }

    [Fact]
    public async Task ACommandThatIsCancelledStops()
    {
        var runner = TestGit.Runner();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var stopped = false;
        try
        {
            await runner.RunAsync(new GitInvocation(Folder.Root, new[] { "--version" }), cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }

        stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task OutputIsReadAsUtf8()
    {
        var repo = NewRepo();
        var subject = "Caf" + char.ConvertFromUtf32(0xE9) + " " + char.ConvertFromUtf32(0x65E5) + char.ConvertFromUtf32(0x672C);
        var sha = repo.Commit("a.txt", "1", subject);

        var resolution = await Service.ResolveAsync(repo.Path, sha);

        resolution.Commit.ShouldNotBeNull().Subject.ShouldBe(subject);
    }
}
