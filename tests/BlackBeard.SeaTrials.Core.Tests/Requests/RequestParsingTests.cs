using System.Text;
using System.Text.Json.Nodes;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

public class RequestParsingTests
{
    [Fact]
    public void EveryKeyOfTheStepsFormIsRead()
    {
        var file = TestRequests.Load(TestRequests.Complete);

        file.Format.ShouldBe("seatrials/1");
        file.Name.ShouldBe("Complete request");
        file.Purpose.ShouldBe("Exercise every key.");
        file.Repo.Remote.ShouldBe("https://example.com/acme/sample-repo.git");

        var bindings = file.Bindings.ShouldNotBeNull();
        bindings.Select(b => b.Name).ShouldBeSequence("data", "profile", "note", "bare");
        bindings[0].Description.ShouldBe("Folder of input data");
        bindings[0].Kind.ShouldBe(BindingKind.Folder);
        bindings[1].Kind.ShouldBe(BindingKind.File);
        bindings[2].Description.ShouldBeNull();
        bindings[2].Kind.ShouldBe(BindingKind.Text);
        bindings[3].Kind.ShouldBeNull();
        bindings[3].EffectiveKind.ShouldBe(BindingKind.Text);

        file.Revisions.Select(r => r.Label).ShouldBeSequence("before", "after", "pinned-later", "by-id");
        file.Revisions[0].Rev.ShouldBe("1a2b3c4^");
        file.Revisions[0].Sha.ShouldBe(TestRequests.Sha1);
        file.Revisions[2].Sha.ShouldBeNull();
        file.Revisions[3].Rev.ShouldBeNull();
        file.Revisions[3].Sha.ShouldBe(TestRequests.Sha3);

        var steps = file.Steps.ShouldNotBeNull();
        file.Tasks.ShouldBeNull();
        steps.Select(s => s.Name).ShouldBeSequence("overlay", "build", "measure");

        var overlay = steps[0];
        overlay.Kind.ShouldBe(StepKind.Other);
        overlay.Command.ShouldBe("git apply C:\\work\\change.patch");
        overlay.Inputs.ShouldNotBeNull().ShouldBeSequence("C:\\work\\change.patch");

        var build = steps[1];
        build.Kind.ShouldBe(StepKind.Build);
        build.Workdir.ShouldBe("src");
        build.Shell.ShouldBe(ShellKind.Pwsh);
        var env = build.Env.ShouldNotBeNull();
        env.Select(e => e.Name).ShouldBeSequence("CONFIG", "API_TOKEN");
        env[0].Value.ShouldBe("Release");
        env[0].Secret.ShouldBeNull();
        env[0].IsSecret.ShouldBeFalse();
        env[1].Value.ShouldBe("not-a-real-value");
        env[1].IsSecret.ShouldBeTrue();
        build.TimeoutMinutes.ShouldBe(10);
        build.OnFailure.ShouldBe(FailureAction.Continue);
        build.OkExitCodes.ShouldNotBeNull().ShouldBeSequence(0, 2);

        var measure = steps[2];
        measure.Kind.ShouldBe(StepKind.Run);
        measure.HandsOff.ShouldBe(true);
        var artifacts = measure.Artifacts.ShouldNotBeNull();
        artifacts[0].Glob.ShouldBe("{artifactDir}\\*.json");
        artifacts[0].ObjectForm.ShouldBeFalse();
        artifacts[1].Glob.ShouldBe("*.txt");
        artifacts[1].Optional.ShouldBe(true);
        artifacts[1].IncludeOlder.ShouldBe(true);
        var checks = measure.Expect.ShouldNotBeNull();
        checks.Count.ShouldBe(3);
        checks[0].In.ShouldBe("artifact:*.txt");
        checks[0].ArtifactGlob.ShouldBe("*.txt");
        checks[0].InOutput.ShouldBeFalse();
        checks[0].MustMatch.ShouldBe("Build: .*\\+{sha7}");
        checks[0].Message.ShouldBe("the report is not from this commit");
        checks[1].InOutput.ShouldBeTrue();
        checks[1].MustNotMatch.ShouldBe("Unhandled exception");
        checks[2].MustContain.ShouldBe("Done");

        var matrix = file.Matrix.ShouldNotBeNull();
        matrix.Rounds.ShouldBe(2);
        matrix.Order.ShouldBe(MatrixOrder.Blocked);
        matrix.PauseBetweenSeconds.ShouldBe(60);

        var pair = file.Compare.ShouldNotBeNull().ShouldHaveSingleItem();
        pair.Name.ShouldBe("the change");
        pair.A.ShouldBe("before");
        pair.B.ShouldBe("after");

        var generated = file.Generated.ShouldNotBeNull();
        generated.By.ShouldBe("a test");
        generated.At.ShouldBe("2026-11-02T10:00:00Z");
        generated.Tool.ShouldBe("stamp.py 1");
        generated.Baseline.ShouldBe("{}");
    }

    [Fact]
    public void EveryKeyOfTheTasksFormIsRead()
    {
        var file = TestRequests.Load(TestRequests.Tasks);

        file.Steps.ShouldBeNull();
        var tasks = file.Tasks.ShouldNotBeNull();
        tasks.Select(t => t.Name).ShouldBeSequence("build only", "full");
        tasks[0].Revision.ShouldBe("A");
        tasks[0].Steps.Count.ShouldBe(1);
        tasks[1].Revision.ShouldBe("B");
        tasks[1].Steps.Select(s => s.Name).ShouldBeSequence("build", "test");
        tasks[1].Steps[1].Kind.ShouldBe(StepKind.Test);
    }

    [Fact]
    public void AbsentKeysStayAbsentAndTheDefaultsAreApplied()
    {
        var file = TestRequests.Load(TestRequests.Minimal);

        file.Purpose.ShouldBeNull();
        file.Bindings.ShouldBeNull();
        file.Matrix.ShouldBeNull();
        file.Compare.ShouldBeNull();
        file.Generated.ShouldBeNull();
        var step = file.Steps.ShouldNotBeNull().ShouldHaveSingleItem();
        step.Kind.ShouldBeNull();
        step.EffectiveKind.ShouldBe(StepKind.Other);
        step.EffectiveWorkdir.ShouldBe(".");
        step.Shell.ShouldBeNull();
        step.EffectiveOnFailure.ShouldBe(FailureAction.Stop);
        step.EffectiveOkExitCodes.ShouldBeSequence(0);
        step.EffectiveHandsOff.ShouldBeFalse();
        step.TimeoutMinutes.ShouldBeNull();
        new Matrix().EffectiveRounds.ShouldBe(1);
        new Matrix().EffectiveOrder.ShouldBe(MatrixOrder.Interleaved);
        new Matrix().EffectivePauseBetweenSeconds.ShouldBe(0);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("tasks")]
    [InlineData("minimal")]
    public void WritingGivesBackTheJsonThatWasRead(string which)
    {
        var json = which switch
        {
            "complete" => TestRequests.Complete,
            "tasks" => TestRequests.Tasks,
            _ => TestRequests.Minimal,
        };

        var written = TestRequests.Load(json).ToJson();

        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(written)).ShouldBeTrue(written);
        TestRequests.Load(written).ToJson().ShouldBe(written);
    }

    [Fact]
    public void TheSampleRequestIsValidAndRoundTrips()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "report-before-after.seatrials.json");
        var json = File.ReadAllText(path);

        var file = TestRequests.Load(json);

        file.Revisions.Select(r => r.Label).ShouldBeSequence("before", "after");
        JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(file.ToJson())).ShouldBeTrue();
    }

    [Fact]
    public void TheWrittenFormUsesTwoSpacesAndLineFeeds()
    {
        var written = TestRequests.Load(TestRequests.Minimal).ToJson();

        written.ShouldStartWith("{\n  \"format\": \"seatrials/1\",\n  \"name\": \"Minimal\",\n");
        written.ShouldNotContain("\r");
        written.EndsWith("}\n", StringComparison.Ordinal).ShouldBeTrue();
    }

    [Fact]
    public void NonAsciiTextIsWrittenAsItIs()
    {
        var edit = TestRequests.Edit(TestRequests.Minimal);
        edit["purpose"] = "Does <this> change the time \u00B7 on caf\u00E9?";

        var written = TestRequests.Load(edit.ToJsonString()).ToJson();

        written.ShouldContain("Does <this> change the time \u00B7 on caf\u00E9?");
    }

    [Fact]
    public void ThePlanIsTheFileWithoutTheStamp()
    {
        var file = TestRequests.Load(TestRequests.Complete);

        var plan = JsonNode.Parse(file.ToJson(includeGenerated: false)).ShouldNotBeNull().AsObject();

        plan.ContainsKey("generated").ShouldBeFalse();
        plan.ContainsKey("steps").ShouldBeTrue();
        JsonNode.Parse(file.ToJson()).ShouldNotBeNull().AsObject().ContainsKey("generated").ShouldBeTrue();
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        const string json = """
            // A request written by hand.
            {
              "format": "seatrials/1", /* inline */
              "name": "Hand written",
              "repo": { "remote": "https://example.com/acme/sample-repo.git", },
              "revisions": [ { "label": "only", "rev": "main", }, ],
              "steps": [ { "name": "run", "command": "echo hi", }, ],
            }
            """;

        TestRequests.Load(json).Name.ShouldBe("Hand written");
    }

    [Fact]
    public void AByteOrderMarkIsTolerated()
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(TestRequests.Minimal))
            .ToArray();

        var result = RequestFile.ParseBytes(bytes);

        result.IsValid.ShouldBeTrue();
        RequestFile.Parse("\uFEFF" + TestRequests.Minimal).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AnUnstampedFileIsValid()
    {
        TestRequests.Load(TestRequests.Minimal).Generated.ShouldBeNull();
    }

    [Fact]
    public void AnEditedModelIsValidatedWithoutParsing()
    {
        var file = TestRequests.Load(TestRequests.Minimal);
        var broken = file with { Steps = new List<Step> { new("", " ") } };

        var issues = RequestValidator.Validate(broken);

        issues.Any(i => i.Code == "empty-value" && i.Path == "steps[0].name").ShouldBeTrue();
        issues.Any(i => i.Code == "empty-value" && i.Path == "steps[0].command").ShouldBeTrue();
        RequestValidator.Validate(file).Where(i => i.IsError).ShouldBeEmpty();
    }
}
