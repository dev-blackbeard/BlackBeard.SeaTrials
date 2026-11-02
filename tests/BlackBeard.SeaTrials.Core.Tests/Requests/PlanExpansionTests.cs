using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

public class PlanExpansionTests
{
    private static RequestFile TwoRevisions(Action<System.Text.Json.Nodes.JsonObject>? edit = null)
    {
        var root = TestRequests.Edit(TestRequests.Minimal);
        root["revisions"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["label"] = "A", ["sha"] = TestRequests.Sha1 },
            new System.Text.Json.Nodes.JsonObject { ["label"] = "B", ["rev"] = "alpha/topic" });
        edit?.Invoke(root);
        return TestRequests.Load(root.ToJsonString());
    }

    private static string[] Names(IReadOnlyList<PlannedTask> plan) => plan.Select(t => t.Name).ToArray();

    private const string Dot = " \u00B7 round ";

    [Fact]
    public void InterleavedRunsTheRevisionsInTurn()
    {
        var file = TwoRevisions(r => r["matrix"] = new System.Text.Json.Nodes.JsonObject { ["rounds"] = 3, ["order"] = "interleaved" });

        var plan = PlanExpander.Expand(file);

        Names(plan).ShouldBeSequence(
            "A" + Dot + "1", "B" + Dot + "1", "A" + Dot + "2", "B" + Dot + "2", "A" + Dot + "3", "B" + Dot + "3");
        plan.Select(t => t.Index).ShouldBeSequence(1, 2, 3, 4, 5, 6);
        plan.Select(t => t.Round).ShouldBeSequence(1, 1, 2, 2, 3, 3);
    }

    [Fact]
    public void BlockedRunsAllRoundsOfARevisionTogether()
    {
        var file = TwoRevisions(r => r["matrix"] = new System.Text.Json.Nodes.JsonObject { ["rounds"] = 3, ["order"] = "blocked" });

        var plan = PlanExpander.Expand(file);

        Names(plan).ShouldBeSequence(
            "A" + Dot + "1", "A" + Dot + "2", "A" + Dot + "3", "B" + Dot + "1", "B" + Dot + "2", "B" + Dot + "3");
        plan.Select(t => t.Round).ShouldBeSequence(1, 2, 3, 1, 2, 3);
    }

    [Fact]
    public void WithoutAMatrixEachRevisionRunsOnceAsRoundOne()
    {
        var plan = PlanExpander.Expand(TwoRevisions());

        Names(plan).ShouldBeSequence("A" + Dot + "1", "B" + Dot + "1");
    }

    [Fact]
    public void AMatrixWithoutRoundsOrOrderUsesTheDefaults()
    {
        var plan = PlanExpander.Expand(TwoRevisions(r => r["matrix"] = new System.Text.Json.Nodes.JsonObject { ["pauseBetweenSeconds"] = 5 }));

        Names(plan).ShouldBeSequence("A" + Dot + "1", "B" + Dot + "1");
    }

    [Fact]
    public void EachTaskCarriesItsRevisionAndTheSharedSteps()
    {
        var file = TwoRevisions();

        var plan = PlanExpander.Expand(file);

        plan[0].RevisionLabel.ShouldBe("A");
        plan[0].Sha.ShouldBe(TestRequests.Sha1);
        plan[0].Rev.ShouldBeNull();
        plan[1].RevisionLabel.ShouldBe("B");
        plan[1].Sha.ShouldBeNull();
        plan[1].Rev.ShouldBe("alpha/topic");
        plan[0].Steps.Select(s => s.Name).ShouldBeSequence("run");
        plan[1].Steps.Select(s => s.Name).ShouldBeSequence("run");
    }

    [Fact]
    public void ExplicitTasksAreExpandedToo()
    {
        var file = TestRequests.Load(TestRequests.Tasks);

        var plan = PlanExpander.Expand(file);

        Names(plan).ShouldBeSequence("build only" + Dot + "1", "full" + Dot + "1");
        plan[0].RevisionLabel.ShouldBe("A");
        plan[1].RevisionLabel.ShouldBe("B");
        plan[1].Steps.Select(s => s.Name).ShouldBeSequence("build", "test");
    }

    [Fact]
    public void ExplicitTasksRepeatEveryRound()
    {
        var root = TestRequests.Edit(TestRequests.Tasks);
        root["matrix"] = new System.Text.Json.Nodes.JsonObject { ["rounds"] = 2 };

        var plan = PlanExpander.Expand(TestRequests.Load(root.ToJsonString()));

        Names(plan).ShouldBeSequence(
            "build only" + Dot + "1", "full" + Dot + "1", "build only" + Dot + "2", "full" + Dot + "2");
    }

    [Fact]
    public void TheCompleteRequestExpandsFourRevisionsByTwoRoundsBlocked()
    {
        var plan = PlanExpander.Expand(TestRequests.Load(TestRequests.Complete));

        plan.Count.ShouldBe(8);
        Names(plan).Take(3).ShouldBeSequence("before" + Dot + "1", "before" + Dot + "2", "after" + Dot + "1");
        plan[7].Name.ShouldBe("by-id" + Dot + "2");
    }

    [Fact]
    public void StaticValuesFeedThePlaceholders()
    {
        var plan = PlanExpander.Expand(TwoRevisions(r => r["matrix"] = new System.Text.Json.Nodes.JsonObject { ["rounds"] = 2 }));

        var values = plan[2].StaticValues(new Dictionary<string, string> { ["data"] = "D" });

        Placeholders.Expand("{label}/{round}/{task}/{sha7}/{bind:data}", values).ShouldBe("A/2/A/0000000/D");
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{sha}", plan[3].StaticValues()));
    }

    [Fact]
    public void AnInvalidRequestIsNotExpanded()
    {
        var file = TestRequests.Load(TestRequests.Minimal) with { Steps = null };

        Should.Throw<InvalidOperationException>(() => PlanExpander.Expand(file));
    }

    [Fact]
    public void TheSampleExpandsToTheDocumentedPlan()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "report-before-after.seatrials.json");

        var plan = PlanExpander.Expand(TestRequests.Load(File.ReadAllText(path)));

        Names(plan).ShouldBeSequence("before" + Dot + "1", "after" + Dot + "1", "before" + Dot + "2", "after" + Dot + "2");
    }
}
