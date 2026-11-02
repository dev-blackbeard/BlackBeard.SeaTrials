using System.Text.RegularExpressions;

namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>
/// The git commands the harness may run, and the only place that says so (SPEC section 5.4). It reads the user's
/// clone and fetches into it; it never checks out, resets, cleans, stashes, pulls or merges there. A command that is
/// not on this list is refused before any process is started.
/// </summary>
public static class GitAllowList
{
    /// <summary>The list as the specification gives it, for messages and for the test that pins it.</summary>
    public static readonly IReadOnlyList<string> Commands = new[]
    {
        "--version", "rev-parse", "log", "for-each-ref", "show", "status", "fetch",
        "worktree add", "worktree remove", "worktree prune", "worktree list",
        "cat-file", "submodule", "remote get-url",
    };

    private static readonly Regex FullSha = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> FetchFlags = new(StringComparer.Ordinal) { "--all", "--tags", "--prune", "--quiet" };

    public static bool IsAllowed(IReadOnlyList<string> arguments, GitScope scope) => Refusal(arguments, scope) is null;

    public static void EnsureAllowed(IReadOnlyList<string> arguments, GitScope scope)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var reason = Refusal(arguments, scope);
        if (reason is not null)
        {
            throw new GitCommandNotAllowedException(arguments, reason);
        }
    }

    /// <summary>Why the command is refused, or null when it is allowed.</summary>
    public static string? Refusal(IReadOnlyList<string> arguments, GitScope scope)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return "there is no command";
        }

        if (arguments.Skip(1).Any(IsOutputOption))
        {
            return "no command may write to a file of its choosing (--output)";
        }

        var command = arguments[0];
        switch (command)
        {
            case "--version":
                return arguments.Count == 1 ? null : "--version takes no arguments";

            case "rev-parse":
            case "log":
            case "for-each-ref":
            case "show":
            case "status":
            case "cat-file":
                return null;

            case "fetch":
                return FetchRefusal(arguments);

            case "worktree":
                return WorktreeRefusal(arguments);

            case "submodule":
                return SubmoduleRefusal(arguments, scope);

            case "remote":
                return arguments.Count == 3 && arguments[1] == "get-url" && !arguments[2].StartsWith('-')
                    ? null
                    : "only 'remote get-url <name>' is allowed";

            default:
                return "'" + command + "' is not one of the git commands the harness may run";
        }
    }

    private static bool IsOutputOption(string argument) =>
        argument.StartsWith("--output", StringComparison.Ordinal);

    // Only the flags of "git fetch --all --tags --prune". A remote or a refspec could overwrite the user's branches.
    private static string? FetchRefusal(IReadOnlyList<string> arguments)
    {
        for (var i = 1; i < arguments.Count; i++)
        {
            if (!FetchFlags.Contains(arguments[i]))
            {
                return "fetch takes only --all, --tags, --prune and --quiet, never a remote, a refspec or --force";
            }
        }

        return null;
    }

    private static string? WorktreeRefusal(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2)
        {
            return "worktree needs add, remove, prune or list";
        }

        switch (arguments[1])
        {
            case "add":
                return arguments.Count == 5
                    && arguments[2] == "--detach"
                    && IsPath(arguments[3])
                    && FullSha.IsMatch(arguments[4])
                    ? null
                    : "only 'worktree add --detach <path> <full commit id>' is allowed";

            case "remove":
                return arguments.Count == 4 && arguments[2] == "--force" && IsPath(arguments[3])
                    ? null
                    : "only 'worktree remove --force <path>' is allowed";

            case "prune":
                return arguments.Count == 2 ? null : "worktree prune takes no arguments";

            case "list":
                return arguments.Count == 2 || (arguments.Count == 3 && arguments[2] == "--porcelain")
                    ? null
                    : "only 'worktree list [--porcelain]' is allowed";

            default:
                return "worktree " + arguments[1] + " is not allowed";
        }
    }

    private static string? SubmoduleRefusal(IReadOnlyList<string> arguments, GitScope scope)
    {
        if (scope != GitScope.OwnWorktree)
        {
            return "submodule commands run only in a worktree the harness made, never in the user's clone";
        }

        var expected = new[] { "submodule", "update", "--init", "--recursive" };
        return arguments.SequenceEqual(expected, StringComparer.Ordinal)
            ? null
            : "only 'submodule update --init --recursive' is allowed";
    }

    private static bool IsPath(string argument) => argument.Length > 0 && !argument.StartsWith('-');
}
