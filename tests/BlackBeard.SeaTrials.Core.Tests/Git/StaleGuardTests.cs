using BlackBeard.SeaTrials.Core.Git;
using BlackBeard.SeaTrials.Core.Tests.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

/// <summary>The decision of the stale-commit guard (SPEC 5.3), on states made up by hand; the real worktrees are in WorktreeTests.</summary>
public class StaleGuardTests
{
    private static readonly string Pinned = new('1', 40);
    private static readonly string Other = new('2', 40);

    [Fact]
    public void ACleanWorktreeAtThePinnedCommitMayRun()
    {
        var result = StaleGuard.CheckBeforeFirstStep(new WorktreeState(Pinned, Array.Empty<string>()), Pinned);

        result.Verdict.ShouldBe(GuardVerdict.Ok);
        result.IsOk.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void AWorktreeAtAnotherCommitIsStaleAndNothingRuns()
    {
        var result = StaleGuard.CheckBeforeFirstStep(new WorktreeState(Other, Array.Empty<string>()), Pinned);

        result.Verdict.ShouldBe(GuardVerdict.Stale);
        result.IsOk.ShouldBeFalse();
        result.Message.ShouldContain(Other);
        result.Message.ShouldContain(Pinned);
        result.Message.ShouldContain("Nothing was run");
    }

    [Fact]
    public void AWorktreeThatIsNotCleanBeforeTheFirstStepDoesNotRun()
    {
        var result = StaleGuard.CheckBeforeFirstStep(new WorktreeState(Pinned, new[] { "?? leftover.txt" }), Pinned);

        result.Verdict.ShouldBe(GuardVerdict.NotClean);
        result.Message.ShouldContain("not clean");
    }

    [Fact]
    public void StaleWinsOverNotClean()
    {
        var result = StaleGuard.CheckBeforeFirstStep(new WorktreeState(Other, new[] { " M file.txt" }), Pinned);

        result.Verdict.ShouldBe(GuardVerdict.Stale);
    }

    [Fact]
    public void AfterTheLastStepAMovedHeadIsNotTrusted()
    {
        var result = StaleGuard.CheckAfterLastStep(new WorktreeState(Other, Array.Empty<string>()), Pinned);

        result.Verdict.ShouldBe(GuardVerdict.Stale);
        result.Message.ShouldContain("moved during the run");
    }

    [Fact]
    public void AfterTheLastStepChangesToTrackedFilesAreAWarningNotAFailure()
    {
        var state = new WorktreeState(Pinned, new[] { " M src/a.cs", "M  src/b.cs", "?? build-output.txt", "D  old.txt" });

        var result = StaleGuard.CheckAfterLastStep(state, Pinned);

        result.Verdict.ShouldBe(GuardVerdict.Ok);
        result.Warnings.Count.ShouldBe(3);
        result.Warnings.Any(w => w.Contains("src/a.cs")).ShouldBeTrue();
        result.Warnings.Any(w => w.Contains("src/b.cs")).ShouldBeTrue();
        result.Warnings.Any(w => w.Contains("old.txt")).ShouldBeTrue();
        result.Warnings.Any(w => w.Contains("build-output.txt")).ShouldBeFalse();
    }

    [Fact]
    public void UntrackedFilesAreNotTrackedChanges()
    {
        var state = new WorktreeState(Pinned, new[] { "?? a", "?? b/c" });

        state.TrackedChanges.ShouldBeEmpty();
        StaleGuard.CheckAfterLastStep(state, Pinned).Warnings.ShouldBeEmpty();
    }
}
