namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>One task of the expanded plan: a revision, a round and the steps to run.</summary>
public sealed record PlannedTask(
    int Index,
    string Name,
    string BaseName,
    string RevisionLabel,
    string? Rev,
    string? Sha,
    int Round,
    IReadOnlyList<Step> Steps)
{
    /// <summary>The values known before anything runs: {sha}, {label}, {round}, {task} and the bindings.</summary>
    public PlaceholderValues StaticValues(IReadOnlyDictionary<string, string>? bindings = null) => new()
    {
        Sha = Sha,
        Label = RevisionLabel,
        Round = Round,
        Task = BaseName,
        Bindings = bindings,
    };
}

/// <summary>
/// Expands the revisions (or explicit tasks) and the matrix into the ordered plan. Interleaved runs A, B, A, B;
/// blocked runs A, A, B, B. Every task is named "&lt;base name&gt; · round N", also when there is a single round.
/// </summary>
public static class PlanExpander
{
    public const string RoundSeparator = " · round ";

    public static IReadOnlyList<PlannedTask> Expand(RequestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var errors = RequestValidator.Validate(file).Where(issue => issue.IsError).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "The request is not valid: " + string.Join(", ", errors.Select(e => e.Code + " at " + e.Path)));
        }

        var rounds = file.Matrix?.EffectiveRounds ?? 1;
        var order = file.Matrix?.EffectiveOrder ?? MatrixOrder.Interleaved;
        var byLabel = file.Revisions.ToDictionary(revision => revision.Label, StringComparer.Ordinal);

        var bases = new List<BaseTask>();
        if (file.Tasks is { } tasks)
        {
            foreach (var task in tasks)
            {
                bases.Add(new BaseTask(task.Name, byLabel[task.Revision], task.Steps));
            }
        }
        else
        {
            var steps = file.Steps ?? new List<Step>();
            foreach (var revision in file.Revisions)
            {
                bases.Add(new BaseTask(revision.Label, revision, steps));
            }
        }

        var planned = new List<PlannedTask>();
        if (order == MatrixOrder.Interleaved)
        {
            for (var round = 1; round <= rounds; round++)
            {
                foreach (var task in bases)
                {
                    planned.Add(Make(planned.Count + 1, task, round));
                }
            }
        }
        else
        {
            foreach (var task in bases)
            {
                for (var round = 1; round <= rounds; round++)
                {
                    planned.Add(Make(planned.Count + 1, task, round));
                }
            }
        }

        return planned;
    }

    private static PlannedTask Make(int index, BaseTask task, int round) => new(
        index,
        task.Name + RoundSeparator + round,
        task.Name,
        task.Revision.Label,
        task.Revision.Rev,
        task.Revision.Sha,
        round,
        task.Steps);

    private sealed record BaseTask(string Name, Revision Revision, IReadOnlyList<Step> Steps);
}
