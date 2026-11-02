using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>One {name} or {name:argument} found in a text.</summary>
public sealed record PlaceholderToken(string Name, string? Argument, int Start, int Length);

/// <summary>A placeholder that has no value in this context.</summary>
public sealed class PlaceholderException : Exception
{
    public PlaceholderException(string message) : base(message)
    {
    }
}

/// <summary>
/// The text substitution used in commands, working directories, environment values, checks, artifact globs and
/// inputs. A placeholder is {name} or {name:argument} where the name starts with a letter. Anything else in braces
/// is plain text (a regular expression like \d{3}, a script block like { echo hi }). To write a literal opening
/// brace in front of a name, double it: {{sha} is the text {sha}.
/// </summary>
public static class Placeholders
{
    public const string BindName = "bind";

    /// <summary>The placeholders that take no argument. {bind:name} is the one that takes one.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "sha", "sha7", "label", "worktree", "runDir", "artifactDir", "round", "task", "step",
    };

    private static readonly Regex TokenPattern = new(
        @"\G\{([A-Za-z][A-Za-z0-9]*)(?::([^{}]*))?\}",
        RegexOptions.CultureInvariant);

    /// <summary>Every placeholder in the text, in order.</summary>
    public static IReadOnlyList<PlaceholderToken> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<PlaceholderToken>();
        foreach (var (_, token) in Tokenize(text))
        {
            if (token is not null)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    /// <summary>Replaces each placeholder using the callback; the text between them, and the {{ escape, are resolved.</summary>
    public static string Substitute(string text, Func<PlaceholderToken, string> replace)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(replace);
        var result = new StringBuilder();
        foreach (var (literal, token) in Tokenize(text))
        {
            result.Append(token is null ? literal : replace(token));
        }

        return result.ToString();
    }

    /// <summary>Expands every placeholder or throws <see cref="PlaceholderException"/> for one without a value.</summary>
    public static string Expand(string text, PlaceholderValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Substitute(text, values.Resolve);
    }

    private static IEnumerable<(string? Literal, PlaceholderToken? Token)> Tokenize(string text)
    {
        var literal = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{')
                {
                    literal.Append('{');
                    i += 2;
                    continue;
                }

                var match = TokenPattern.Match(text, i);
                if (match.Success)
                {
                    if (literal.Length > 0)
                    {
                        yield return (literal.ToString(), null);
                        literal.Clear();
                    }

                    var argument = match.Groups[2].Success ? match.Groups[2].Value : null;
                    yield return (null, new PlaceholderToken(match.Groups[1].Value, argument, i, match.Length));
                    i += match.Length;
                    continue;
                }
            }

            literal.Append(text[i]);
            i++;
        }

        if (literal.Length > 0)
        {
            yield return (literal.ToString(), null);
        }
    }
}

/// <summary>The values that placeholders take for one step of one task. A value that is not set cannot be expanded.</summary>
public sealed class PlaceholderValues
{
    public string? Sha { get; init; }

    public string? Label { get; init; }

    public string? Worktree { get; init; }

    public string? RunDir { get; init; }

    public string? ArtifactDir { get; init; }

    public int? Round { get; init; }

    public string? Task { get; init; }

    public string? Step { get; init; }

    public IReadOnlyDictionary<string, string>? Bindings { get; init; }

    public string Resolve(PlaceholderToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Name == Placeholders.BindName)
        {
            if (string.IsNullOrEmpty(token.Argument))
            {
                throw new PlaceholderException("{bind:} needs the name of a binding.");
            }

            if (Bindings is not null && Bindings.TryGetValue(token.Argument, out var bound))
            {
                return bound;
            }

            throw new PlaceholderException("The binding \"" + token.Argument + "\" has no value.");
        }

        if (token.Argument is not null)
        {
            throw new PlaceholderException("{" + token.Name + "} takes no argument.");
        }

        return token.Name switch
        {
            "sha" => Need(Sha, token),
            "sha7" => Need(Sha, token).Substring(0, Math.Min(7, Need(Sha, token).Length)),
            "label" => Need(Label, token),
            "worktree" => Need(Worktree, token),
            "runDir" => Need(RunDir, token),
            "artifactDir" => Need(ArtifactDir, token),
            "round" => Round is { } round
                ? round.ToString(CultureInfo.InvariantCulture)
                : throw Missing(token),
            "task" => Need(Task, token),
            "step" => Need(Step, token),
            _ => throw new PlaceholderException("Unknown placeholder {" + token.Name + "}."),
        };
    }

    private static string Need(string? value, PlaceholderToken token) => value ?? throw Missing(token);

    private static PlaceholderException Missing(PlaceholderToken token) =>
        new("The placeholder {" + token.Name + "} has no value here.");
}
