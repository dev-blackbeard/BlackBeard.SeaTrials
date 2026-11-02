namespace BlackBeard.SeaTrials.Core.Requests;

public enum Severity { Error, Warning }

/// <summary>One thing wrong with a request file. <c>Code</c> is stable; <c>Path</c> is like steps[1].expect[0].mustMatch.</summary>
public sealed record ValidationIssue(Severity Severity, string Code, string Path, string Message)
{
    public bool IsError => Severity == Severity.Error;

    public override string ToString() =>
        (Severity == Severity.Error ? "error " : "warning ") + Code + " at " + (Path.Length == 0 ? "(file)" : Path) + ": " + Message;
}

public sealed class ParseResult
{
    public ParseResult(RequestFile? file, IReadOnlyList<ValidationIssue> issues)
    {
        File = file;
        Issues = issues;
    }

    /// <summary>The parsed request, or null when the file cannot be read into a request at all.</summary>
    public RequestFile? File { get; }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IEnumerable<ValidationIssue> Errors => Issues.Where(i => i.IsError);

    public IEnumerable<ValidationIssue> Warnings => Issues.Where(i => !i.IsError);

    public bool IsValid => File is not null && !Errors.Any();

    internal static ParseResult Failed(string code, string message) =>
        new(null, new[] { new ValidationIssue(Severity.Error, code, "", message) });
}
