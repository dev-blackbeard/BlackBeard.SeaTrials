namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>The keys of each object in a request file. The JSON schema must list the same keys; a test pins it.</summary>
public static class RequestKeys
{
    public static readonly IReadOnlySet<string> Root = Set(
        "format", "name", "purpose", "repo", "bindings", "revisions", "steps", "tasks", "matrix", "compare", "generated");

    public static readonly IReadOnlySet<string> Repo = Set("remote");

    public static readonly IReadOnlySet<string> Binding = Set("description", "kind");

    public static readonly IReadOnlySet<string> Revision = Set("label", "rev", "sha");

    public static readonly IReadOnlySet<string> Step = Set(
        "name", "kind", "command", "workdir", "shell", "env", "timeoutMinutes", "onFailure", "okExitCodes",
        "handsOff", "expect", "artifacts", "inputs");

    public static readonly IReadOnlySet<string> EnvObject = Set("value", "secret");

    public static readonly IReadOnlySet<string> Check = Set("in", "mustContain", "mustMatch", "mustNotMatch", "message");

    public static readonly IReadOnlySet<string> Artifact = Set("glob", "optional", "includeOlder");

    public static readonly IReadOnlySet<string> Task = Set("name", "revision", "steps");

    public static readonly IReadOnlySet<string> Matrix = Set("rounds", "order", "pauseBetweenSeconds");

    public static readonly IReadOnlySet<string> Compare = Set("name", "a", "b");

    public static readonly IReadOnlySet<string> Generated = Set("by", "at", "tool", "baselineSha256", "baseline");

    private static IReadOnlySet<string> Set(params string[] keys) => new HashSet<string>(keys, StringComparer.Ordinal);
}

/// <summary>The spelling of each enumeration in a request file.</summary>
internal static class EnumNames
{
    public static readonly IReadOnlyDictionary<string, StepKind> StepKinds = new Dictionary<string, StepKind>
    {
        ["build"] = StepKind.Build,
        ["test"] = StepKind.Test,
        ["run"] = StepKind.Run,
        ["other"] = StepKind.Other,
    };

    public static readonly IReadOnlyDictionary<string, ShellKind> Shells = new Dictionary<string, ShellKind>
    {
        ["cmd"] = ShellKind.Cmd,
        ["pwsh"] = ShellKind.Pwsh,
    };

    public static readonly IReadOnlyDictionary<string, FailureAction> FailureActions = new Dictionary<string, FailureAction>
    {
        ["stop"] = FailureAction.Stop,
        ["continue"] = FailureAction.Continue,
    };

    public static readonly IReadOnlyDictionary<string, BindingKind> BindingKinds = new Dictionary<string, BindingKind>
    {
        ["file"] = BindingKind.File,
        ["folder"] = BindingKind.Folder,
        ["text"] = BindingKind.Text,
    };

    public static readonly IReadOnlyDictionary<string, MatrixOrder> MatrixOrders = new Dictionary<string, MatrixOrder>
    {
        ["interleaved"] = MatrixOrder.Interleaved,
        ["blocked"] = MatrixOrder.Blocked,
    };

    public static string NameOf<T>(IReadOnlyDictionary<string, T> names, T value) where T : struct, Enum =>
        names.First(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key;
}
