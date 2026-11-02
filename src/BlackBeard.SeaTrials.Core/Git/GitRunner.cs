namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>What the launcher is asked to start: a resolved process, with no policy left to apply.</summary>
public sealed record GitLaunch(
    string Executable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> RemovedEnvironment,
    TimeSpan Timeout,
    GitScope Scope);

/// <summary>Starts a git process. Everything that decides whether a command may run happens before this.</summary>
public interface IGitProcessLauncher
{
    Task<GitResult> LaunchAsync(GitLaunch launch, CancellationToken cancellationToken);
}

public sealed class GitProcessOptions
{
    /// <summary>The git to run: "git" on the path, unless Settings point at another.</summary>
    public string GitExecutable { get; init; } = "git";

    /// <summary>Extra environment variables for every command. They win over the harness's own.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Extra configuration for every command, as key and value (needs git 2.31 or later; older git ignores it).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Config { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// The one place that runs git. A command is checked against <see cref="GitAllowList"/> first; the process then runs
/// with the user's own credentials and configuration, without prompting, without refreshing the index of the user's
/// checkout, in English, and without any repository variable inherited from whatever started the harness.
/// </summary>
public sealed class GitRunner
{
    private static readonly string[] RemovedVariables =
    {
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_PREFIX", "GIT_CONFIG_COUNT",
    };

    private static readonly KeyValuePair<string, string>[] BaseConfig =
    {
        new("core.quotepath", "off"),
        new("gc.auto", "0"),
        new("maintenance.auto", "false"),
        new("advice.detachedHead", "false"),
    };

    private readonly GitProcessOptions _options;
    private readonly IGitProcessLauncher _launcher;

    public GitRunner(GitProcessOptions? options = null, IGitProcessLauncher? launcher = null)
    {
        _options = options ?? new GitProcessOptions();
        _launcher = launcher ?? new SystemGitProcessLauncher();
    }

    public async Task<GitResult> RunAsync(GitInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        GitAllowList.EnsureAllowed(invocation.Arguments, invocation.Scope);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };
        var config = BaseConfig.Concat(_options.Config).ToList();
        environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            var index = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment["GIT_CONFIG_KEY_" + index] = config[i].Key;
            environment["GIT_CONFIG_VALUE_" + index] = config[i].Value;
        }

        foreach (var pair in _options.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        var launch = new GitLaunch(
            _options.GitExecutable,
            invocation.WorkingDirectory,
            invocation.Arguments,
            environment,
            RemovedVariables,
            _options.Timeout,
            invocation.Scope);
        return await _launcher.LaunchAsync(launch, cancellationToken).ConfigureAwait(false);
    }
}
