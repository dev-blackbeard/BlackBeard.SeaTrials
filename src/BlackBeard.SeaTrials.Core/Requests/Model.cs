namespace BlackBeard.SeaTrials.Core.Requests;

public enum StepKind { Build, Test, Run, Other }

public enum ShellKind { Cmd, Pwsh }

public enum FailureAction { Stop, Continue }

public enum BindingKind { File, Folder, Text }

public enum MatrixOrder { Interleaved, Blocked }

/// <summary>The repository, named by its remote address. The path of a clone is a property of the PC, not of the request.</summary>
public sealed record RepoRef(string Remote);

/// <summary>A value only the PC running the request knows, supplied once and used as {bind:name}.</summary>
public sealed record BindingSpec(string Name, string? Description = null, BindingKind? Kind = null)
{
    public BindingKind EffectiveKind => Kind ?? BindingKind.Text;
}

/// <summary>A commit to run. <c>Sha</c> is authoritative; <c>Rev</c> is what a person reads.</summary>
public sealed record Revision(string Label, string? Rev = null, string? Sha = null);

/// <summary>An environment variable of a step. <c>ObjectForm</c> remembers that the file spelled it as an object.</summary>
public sealed record EnvVar(string Name, string Value, bool? Secret = null, bool ObjectForm = false)
{
    public bool IsSecret => Secret ?? false;
}

/// <summary>A glob of files to collect. <c>ObjectForm</c> remembers that the file spelled it as an object.</summary>
public sealed record ArtifactSpec(string Glob, bool? Optional = null, bool? IncludeOlder = null, bool ObjectForm = false);

/// <summary>A condition on a step's output or on a collected file. Exactly one of MustContain, MustMatch and MustNotMatch is set.</summary>
public sealed record Check(
    string In,
    string? MustContain = null,
    string? MustMatch = null,
    string? MustNotMatch = null,
    string? Message = null)
{
    public const string OutputTarget = "output";
    public const string ArtifactPrefix = "artifact:";

    public bool InOutput => In == OutputTarget;

    /// <summary>The glob after "artifact:", or null when the check is on the output.</summary>
    public string? ArtifactGlob =>
        In.StartsWith(ArtifactPrefix, StringComparison.Ordinal) ? In.Substring(ArtifactPrefix.Length) : null;
}

public sealed record Step(
    string Name,
    string Command,
    StepKind? Kind = null,
    string? Workdir = null,
    ShellKind? Shell = null,
    IReadOnlyList<EnvVar>? Env = null,
    int? TimeoutMinutes = null,
    FailureAction? OnFailure = null,
    IReadOnlyList<int>? OkExitCodes = null,
    bool? HandsOff = null,
    IReadOnlyList<Check>? Expect = null,
    IReadOnlyList<ArtifactSpec>? Artifacts = null,
    IReadOnlyList<string>? Inputs = null)
{
    private static readonly int[] DefaultOkExitCodes = { 0 };

    public StepKind EffectiveKind => Kind ?? StepKind.Other;

    public string EffectiveWorkdir => Workdir ?? ".";

    public FailureAction EffectiveOnFailure => OnFailure ?? FailureAction.Stop;

    public IReadOnlyList<int> EffectiveOkExitCodes => OkExitCodes ?? DefaultOkExitCodes;

    public bool EffectiveHandsOff => HandsOff ?? false;
}

/// <summary>An explicit task: one revision, by label, with its own steps.</summary>
public sealed record TaskSpec(string Name, string Revision, IReadOnlyList<Step> Steps);

public sealed record Matrix(int? Rounds = null, MatrixOrder? Order = null, int? PauseBetweenSeconds = null)
{
    public int EffectiveRounds => Rounds ?? 1;

    public MatrixOrder EffectiveOrder => Order ?? MatrixOrder.Interleaved;

    public int EffectivePauseBetweenSeconds => PauseBetweenSeconds ?? 0;
}

/// <summary>A pair of revisions, by label, to be read side by side.</summary>
public sealed record ComparePair(string Name, string A, string B);

/// <summary>The stamp: who wrote the request and its baseline. It is the only key outside the plan.</summary>
public sealed record Generated(string By, string At, string Tool, string BaselineSha256, string Baseline);

/// <summary>
/// A request file. Optional keys that were absent stay null, so that writing a parsed file gives back the
/// JSON it was read from; the Effective members apply the defaults.
/// </summary>
public sealed record RequestFile(
    string Format,
    string Name,
    string? Purpose,
    RepoRef Repo,
    IReadOnlyList<BindingSpec>? Bindings,
    IReadOnlyList<Revision> Revisions,
    IReadOnlyList<Step>? Steps,
    IReadOnlyList<TaskSpec>? Tasks,
    Matrix? Matrix,
    IReadOnlyList<ComparePair>? Compare,
    Generated? Generated)
{
    public const string FormatId = "seatrials/1";

    public static ParseResult Parse(string json) => RequestReader.Parse(json);

    public static ParseResult ParseBytes(byte[] utf8) => RequestReader.ParseBytes(utf8);

    public string ToJson() => RequestWriter.ToJson(this);
}
