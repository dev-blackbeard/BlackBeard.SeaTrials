using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBeard.SeaTrials.Core.Provenance;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Provenance;

public class ProvenanceTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    // A stamped request, as JSON that a test can then edit "by hand".
    private static JsonObject Stamped(string json = TestRequests.Complete)
    {
        var stamped = Stamper.Stamp(TestRequests.Load(json), "a test", Morning);
        return TestRequests.Edit(stamped.ToJson());
    }

    private static ProvenanceReport Evaluate(JsonObject root)
    {
        var file = TestRequests.Parse(root).File.ShouldNotBeNull();
        return ProvenanceCheck.Evaluate(file, Noon);
    }

    private static Deviation At(ProvenanceReport report, string path) =>
        report.Deviations.Where(d => d.Path == path).ToList().ShouldHaveSingleItem();

    // ---- the stamp -----------------------------------------------------------------------------------------

    [Fact]
    public void StampingWritesTheFiveKeys()
    {
        var file = TestRequests.Load(TestRequests.Minimal);

        var stamp = Stamper.Stamp(file, "a session", Morning).Generated.ShouldNotBeNull();

        stamp.By.ShouldBe("a session");
        stamp.At.ShouldBe("2026-11-02T10:00:00Z");
        stamp.Tool.ShouldBe("seatrials 1");
        stamp.Baseline.ShouldBe(
            "{\"format\":\"seatrials/1\",\"name\":\"Minimal\","
            + "\"repo\":{\"remote\":\"https://example.com/acme/sample-repo.git\"},"
            + "\"revisions\":[{\"label\":\"only\",\"rev\":\"main\"}],"
            + "\"steps\":[{\"command\":\"echo hello\",\"name\":\"run\"}]}");
        stamp.BaselineSha256.Length.ShouldBe(64);
        stamp.BaselineSha256.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')).ShouldBeTrue();
        stamp.BaselineSha256.ShouldBe(Stamper.Sha256Hex(stamp.Baseline));
    }

    [Fact]
    public void TheTimeIsUtcToTheSecond()
    {
        Stamper.FormatTime(new DateTimeOffset(2026, 11, 2, 11, 30, 5, 987, TimeSpan.FromHours(2))).ShouldBe("2026-11-02T09:30:05Z");
    }

    [Fact]
    public void RestampingReplacesTheOldStampAndNeverEntersTheBaseline()
    {
        var withStamp = TestRequests.Load(TestRequests.Complete);
        withStamp.Generated.ShouldNotBeNull();

        var restamped = Stamper.Stamp(withStamp, "someone else", Noon);

        var stamp = restamped.Generated.ShouldNotBeNull();
        stamp.By.ShouldBe("someone else");
        stamp.Baseline.ShouldNotContain("generated");
        stamp.Baseline.ShouldBe(Stamper.Baseline(withStamp with { Generated = null }));
        restamped.Name.ShouldBe(withStamp.Name);
    }

    [Fact]
    public void AStampNeedsSomeoneWhoWroteIt()
    {
        var file = TestRequests.Load(TestRequests.Minimal);

        Should.Throw<ArgumentException>(() => Stamper.Stamp(file, "  ", Morning));
    }

    [Theory]
    [InlineData("minimal")]
    [InlineData("tasks")]
    [InlineData("complete")]
    public void AStampedFileReadsBackAsRequested(string which)
    {
        var json = which switch
        {
            "minimal" => TestRequests.Minimal,
            "tasks" => TestRequests.Tasks,
            _ => TestRequests.Complete,
        };
        var stamped = Stamper.Stamp(TestRequests.Load(json), "a test", Morning).ToJson();

        var report = ProvenanceCheck.Evaluate(TestRequests.Load(stamped), Noon);

        report.Status.ShouldBe(ProvenanceStatus.AsRequested);
        report.Message.ShouldBe(ProvenanceCheck.AsRequestedMessage);
        report.Deviations.ShouldBeEmpty();
    }

    // ---- unstamped and tampered ----------------------------------------------------------------------------

    [Fact]
    public void AnUnstampedFileCannotBeTold()
    {
        var report = ProvenanceCheck.Evaluate(TestRequests.Load(TestRequests.Minimal), Noon);

        report.Status.ShouldBe(ProvenanceStatus.NotStamped);
        report.Message.ShouldBe("Not stamped: it cannot be told whether this file was changed.");
        report.Deviations.ShouldBeEmpty();
    }

    [Fact]
    public void AnEditedUnstampedFileIsStillJustUnstamped()
    {
        var root = TestRequests.Edit(TestRequests.Minimal);
        TestRequests.StepAt(root, 0)["command"] = "echo changed";

        Evaluate(root).Status.ShouldBe(ProvenanceStatus.NotStamped);
    }

    [Fact]
    public void ABaselineThatWasEditedDoesNotMatchItsHash()
    {
        var root = Stamped();
        var generated = root["generated"]!.AsObject();
        generated["baseline"] = generated["baseline"]!.GetValue<string>().Replace("\"rounds\":2", "\"rounds\":3");

        var report = Evaluate(root);

        report.Status.ShouldBe(ProvenanceStatus.BaselineTampered);
        report.Message.ShouldBe("The baseline in this file has itself been changed; it cannot vouch for what was requested.");
        report.Deviations.ShouldBeEmpty();
    }

    [Fact]
    public void AHashThatWasEditedDoesNotMatchTheBaseline()
    {
        var root = Stamped();
        root["generated"]!.AsObject()["baselineSha256"] = new string('0', 64);

        Evaluate(root).Status.ShouldBe(ProvenanceStatus.BaselineTampered);
    }

    [Fact]
    public void ABaselineThatIsNotAPlanIsRefusedEvenWithAMatchingHash()
    {
        foreach (var baseline in new[] { "not json", "[]", "7", "{ \"unterminated\": " })
        {
            var root = Stamped();
            var generated = root["generated"]!.AsObject();
            generated["baseline"] = baseline;
            generated["baselineSha256"] = Stamper.Sha256Hex(baseline);

            Evaluate(root).Status.ShouldBe(ProvenanceStatus.BaselineTampered, baseline);
        }
    }

    [Fact]
    public void ABaselineAndHashRewrittenTogetherCanVouchForAnEditedPlan()
    {
        // The stamp cannot defend against someone who rewrites both; it detects a plan changed after stamping.
        var root = Stamped();
        TestRequests.StepAt(root, 2)["command"] = "run.exe --other";
        var edited = Stamper.Stamp(TestRequests.Parse(root).File.ShouldNotBeNull(), "someone", Noon);

        ProvenanceCheck.Evaluate(edited, Noon).Status.ShouldBe(ProvenanceStatus.AsRequested);
    }

    // ---- what is and is not a deviation --------------------------------------------------------------------

    [Fact]
    public void TheStampItselfAndTheOrderOfKeysAreNotDeviations()
    {
        var root = Stamped();
        root["generated"]!.AsObject()["by"] = "somebody else";
        root["generated"]!.AsObject()["at"] = "2030-01-01T00:00:00Z";
        var members = root.ToList();
        var reversed = new JsonObject();
        for (var i = members.Count - 1; i >= 0; i--)
        {
            reversed[members[i].Key] = members[i].Value?.DeepClone();
        }

        Evaluate(reversed).Status.ShouldBe(ProvenanceStatus.AsRequested);
    }

    [Fact]
    public void AChangedCommandIsOneDeviationWithBothValues()
    {
        var root = Stamped();
        TestRequests.StepAt(root, 2)["command"] = "run.exe --other";

        var report = Evaluate(root);

        report.Status.ShouldBe(ProvenanceStatus.Deviations);
        report.Message.ShouldBe("Differs from what was requested: 1 change");
        var deviation = report.Deviations.ShouldHaveSingleItem();
        deviation.Path.ShouldBe("steps[measure].command");
        deviation.Kind.ShouldBe(DeviationKind.Changed);
        deviation.Was.ShouldBe("\"run.exe --out {artifactDir} --data {bind:data} --sha {sha}\"");
        deviation.Now.ShouldBe("\"run.exe --other\"");
        deviation.At.ShouldBe(Noon);
        deviation.Reason.ShouldBeNull();
        deviation.Describe().ShouldBe("steps[measure].command: \"run.exe --out {artifactDir} --data {bind:data} --sha {sha}\" \u2192 \"run.exe --other\"");
    }

    [Fact]
    public void ChangesToNumbersRevisionsAndComparisonsHaveReadablePaths()
    {
        var root = Stamped();
        root["matrix"]!.AsObject()["rounds"] = 5;
        TestRequests.RevisionAt(root, 1)["sha"] = TestRequests.Sha3;
        root["compare"]![0]!.AsObject()["b"] = "pinned-later";
        TestRequests.StepAt(root, 1)["timeoutMinutes"] = 20;
        TestRequests.CheckAt(root, 2, 0)["mustMatch"] = "Build: .*";

        var report = Evaluate(root);

        report.Message.ShouldBe("Differs from what was requested: 5 changes");
        var rounds = At(report, "matrix.rounds");
        rounds.Kind.ShouldBe(DeviationKind.Changed);
        rounds.Was.ShouldBe("2");
        rounds.Now.ShouldBe("5");
        At(report, "revisions[after].sha").Now.ShouldBe("\"" + TestRequests.Sha3 + "\"");
        At(report, "compare[0].b").Was.ShouldBe("\"after\"");
        At(report, "steps[build].timeoutMinutes").Was.ShouldBe("10");
        At(report, "steps[measure].expect[0].mustMatch").Now.ShouldBe("\"Build: .*\"");
    }

    [Fact]
    public void AddedKeysStepsRevisionsBindingsAndVariablesAreDeviations()
    {
        var root = Stamped();
        TestRequests.StepAt(root, 0)["timeoutMinutes"] = 5;
        root["steps"]!.AsArray().Add(new JsonObject { ["name"] = "extra", ["command"] = "echo hi" });
        root["revisions"]!.AsArray().Add(new JsonObject { ["label"] = "late", ["rev"] = "main" });
        root["bindings"]!.AsObject()["more"] = new JsonObject();
        TestRequests.StepAt(root, 1)["env"]!.AsObject()["NEW"] = "v";

        var report = Evaluate(root);

        At(report, "steps[overlay].timeoutMinutes").Kind.ShouldBe(DeviationKind.Added);
        At(report, "steps[overlay].timeoutMinutes").Was.ShouldBeNull();
        At(report, "steps[overlay].timeoutMinutes").Now.ShouldBe("5");
        var step = At(report, "steps[extra]");
        step.Kind.ShouldBe(DeviationKind.Added);
        step.Now.ShouldBe("{\"command\":\"echo hi\",\"name\":\"extra\"}");
        At(report, "revisions[late]").Kind.ShouldBe(DeviationKind.Added);
        At(report, "bindings.more").Now.ShouldBe("{}");
        At(report, "steps[build].env.NEW").Now.ShouldBe("\"v\"");
        report.Deviations.Count.ShouldBe(5);
    }

    [Fact]
    public void RemovedKeysStepsAndChecksAreDeviations()
    {
        var root = Stamped();
        root["steps"]!.AsArray().RemoveAt(0);
        TestRequests.StepAt(root, 0).Remove("timeoutMinutes");
        TestRequests.StepAt(root, 1)["expect"]!.AsArray().RemoveAt(2);

        var report = Evaluate(root);

        var step = At(report, "steps[overlay]");
        step.Kind.ShouldBe(DeviationKind.Removed);
        step.Now.ShouldBeNull();
        step.Was.ShouldNotBeNull().ShouldStartWith("{\"command\":\"git apply");
        At(report, "steps[build].timeoutMinutes").Kind.ShouldBe(DeviationKind.Removed);
        var check = At(report, "steps[measure].expect[2]");
        check.Kind.ShouldBe(DeviationKind.Removed);
        check.Was.ShouldBe("{\"in\":\"output\",\"mustContain\":\"Done\"}");
        report.Deviations.Count.ShouldBe(3);
    }

    [Fact]
    public void StepsOrRevisionsInAnotherOrderAreOneReorderedDeviation()
    {
        var root = Stamped();
        var steps = root["steps"]!.AsArray();
        var build = steps[1]!.DeepClone();
        var measure = steps[2]!.DeepClone();
        steps[1] = measure;
        steps[2] = build;
        var revisions = root["revisions"]!.AsArray();
        var first = revisions[0]!.DeepClone();
        var second = revisions[1]!.DeepClone();
        revisions[0] = second;
        revisions[1] = first;

        var report = Evaluate(root);

        report.Deviations.Count.ShouldBe(2);
        var reorderedSteps = At(report, "steps");
        reorderedSteps.Kind.ShouldBe(DeviationKind.Reordered);
        reorderedSteps.Was.ShouldBe("[\"overlay\",\"build\",\"measure\"]");
        reorderedSteps.Now.ShouldBe("[\"overlay\",\"measure\",\"build\"]");
        var reorderedRevisions = At(report, "revisions");
        reorderedRevisions.Kind.ShouldBe(DeviationKind.Reordered);
        reorderedRevisions.Now.ShouldBe("[\"after\",\"before\",\"pinned-later\",\"by-id\"]");
    }

    [Fact]
    public void AListOfValuesInAnotherOrderIsReorderedToo()
    {
        var root = Stamped();
        TestRequests.StepAt(root, 1)["okExitCodes"] = new JsonArray(2, 0);

        var deviation = Evaluate(root).Deviations.ShouldHaveSingleItem();

        deviation.Path.ShouldBe("steps[build].okExitCodes");
        deviation.Kind.ShouldBe(DeviationKind.Reordered);
        deviation.Was.ShouldBe("[0,2]");
        deviation.Now.ShouldBe("[2,0]");
    }

    [Fact]
    public void AChangeInsideAnExplicitTaskIsFoundByTheNamesOfTheTaskAndTheStep()
    {
        var root = Stamped(TestRequests.Tasks);
        TestRequests.TaskAt(root, 1)["steps"]![1]!.AsObject()["command"] = "make test-all";
        root["tasks"]!.AsArray().RemoveAt(0);

        var report = Evaluate(root);

        At(report, "tasks[full].steps[test].command").Kind.ShouldBe(DeviationKind.Changed);
        At(report, "tasks[build only]").Kind.ShouldBe(DeviationKind.Removed);
        report.Deviations.Count.ShouldBe(2);
    }

    [Fact]
    public void AReasonCanBeAttachedToADeviation()
    {
        var root = Stamped();
        TestRequests.StepAt(root, 2)["command"] = "run.exe --other";
        var deviation = Evaluate(root).Deviations.ShouldHaveSingleItem();

        var explained = deviation.WithReason("the data folder moved");

        explained.Reason.ShouldBe("the data folder moved");
        explained.Path.ShouldBe(deviation.Path);
        deviation.Reason.ShouldBeNull();
    }

    [Fact]
    public void SeveralChangesAreCountedInTheMessage()
    {
        var root = Stamped();
        root["matrix"]!.AsObject()["rounds"] = 3;
        root["matrix"]!.AsObject()["pauseBetweenSeconds"] = 0;

        var report = Evaluate(root);

        report.Message.ShouldBe("Differs from what was requested: 2 changes");
    }

    // ---- the finder on its own -----------------------------------------------------------------------------

    private static IReadOnlyList<Deviation> Diff(string before, string after)
    {
        using var left = JsonDocument.Parse(before);
        using var right = JsonDocument.Parse(after);
        return DeviationFinder.Diff(left.RootElement, right.RootElement, Noon);
    }

    [Fact]
    public void ADuplicateNameFallsBackToComparingByPosition()
    {
        var deviations = Diff(
            """{"steps":[{"name":"a","x":1},{"name":"a","x":2}]}""",
            """{"steps":[{"name":"a","x":1},{"name":"a","x":3}]}""");

        var deviation = deviations.ShouldHaveSingleItem();
        deviation.Path.ShouldBe("steps[1].x");
        deviation.Was.ShouldBe("2");
        deviation.Now.ShouldBe("3");
    }

    [Fact]
    public void ATypeChangeIsAChange()
    {
        Diff("""{"a":2,"b":{"c":1}}""", """{"a":"2","b":"s"}""").Select(d => d.Path + "|" + d.Was + "|" + d.Now)
            .ShouldBeSequence("a|2|\"2\"", "b|{\"c\":1}|\"s\"");
    }

    [Fact]
    public void ListsComparedByPositionReportTheTail()
    {
        Diff("""{"inputs":["a"]}""", """{"inputs":["a","b"]}""").ShouldHaveSingleItem().Path.ShouldBe("inputs[1]");
        var removed = Diff("""{"inputs":["a","b"]}""", """{"inputs":["a"]}""").ShouldHaveSingleItem();
        removed.Path.ShouldBe("inputs[1]");
        removed.Kind.ShouldBe(DeviationKind.Removed);
        Diff("""{"inputs":["a","b"]}""", """{"inputs":["a","b"]}""").ShouldBeEmpty();
    }

    [Fact]
    public void ANumberThatIsNotAnIntegerIsShownAsWritten()
    {
        var deviation = Diff("""{"a":1.5}""", """{"a":2}""").ShouldHaveSingleItem();

        deviation.Was.ShouldBe("1.5");
        deviation.Now.ShouldBe("2");
    }

    [Fact]
    public void EqualPlansHaveNoDeviationsWhateverTheSpacing()
    {
        Diff("{ \"a\" : [ 1 , 2 ] , \"b\" : { } }", """{"b":{},"a":[1,2]}""").ShouldBeEmpty();
    }
}
