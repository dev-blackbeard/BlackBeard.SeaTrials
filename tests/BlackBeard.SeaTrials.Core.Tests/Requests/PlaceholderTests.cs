using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

public class PlaceholderTests
{
    private sealed record FieldCase(string Path, Action<System.Text.Json.Nodes.JsonObject> Edit);

    private const string FullSha = "abcdef1-sample-sha";

    private static PlaceholderValues AllValues() => new()
    {
        Sha = FullSha,
        Label = "after",
        Worktree = "C:\\SeaTrials\\work\\SampleRepo\\abcdef1-run",
        RunDir = "C:\\Results\\run1",
        ArtifactDir = "C:\\Results\\run1\\artifacts\\02-measure",
        Round = 3,
        Task = "after",
        Step = "measure",
        Bindings = new Dictionary<string, string> { ["data"] = "C:\\data" },
    };

    [Theory]
    [InlineData("sha", FullSha)]
    [InlineData("sha7", "abcdef1")]
    [InlineData("label", "after")]
    [InlineData("worktree", "C:\\SeaTrials\\work\\SampleRepo\\abcdef1-run")]
    [InlineData("runDir", "C:\\Results\\run1")]
    [InlineData("artifactDir", "C:\\Results\\run1\\artifacts\\02-measure")]
    [InlineData("round", "3")]
    [InlineData("task", "after")]
    [InlineData("step", "measure")]
    public void EverySimplePlaceholderIsFoundAndExpanded(string name, string expectedValue)
    {
        var text = "x {" + name + "} y";

        var tokens = Placeholders.Scan(text);

        var token = tokens.ShouldHaveSingleItem();
        token.Name.ShouldBe(name);
        token.Argument.ShouldBeNull();
        token.Start.ShouldBe(2);
        token.Length.ShouldBe(name.Length + 2);
        Placeholders.Expand(text, AllValues()).ShouldBe("x " + expectedValue + " y");
    }

    [Fact]
    public void TheBindPlaceholderTakesAnArgument()
    {
        var token = Placeholders.Scan("--data {bind:data}").ShouldHaveSingleItem();

        token.Name.ShouldBe("bind");
        token.Argument.ShouldBe("data");
        Placeholders.Expand("--data {bind:data}", AllValues()).ShouldBe("--data C:\\data");
    }

    [Fact]
    public void SeveralPlaceholdersInOneTextAreAllExpanded()
    {
        var text = "{label}-{sha7}-r{round}/{step}";

        Placeholders.Scan(text).Select(t => t.Name).ShouldBeSequence("label", "sha7", "round", "step");
        Placeholders.Expand(text, AllValues()).ShouldBe("after-" + AllValues().Sha!.Substring(0, 7) + "-r3/measure");
    }

    [Theory]
    [InlineData("\\d{3}")]
    [InlineData("a{2,5}")]
    [InlineData("{ echo hi }")]
    [InlineData("@{a=1}")]
    [InlineData("{}")]
    [InlineData("{3}")]
    [InlineData("{sha")]
    [InlineData("sha}")]
    [InlineData("{a b}")]
    public void OtherBracesArePlainText(string text)
    {
        Placeholders.Scan(text).ShouldBeEmpty();
        Placeholders.Expand(text, new PlaceholderValues()).ShouldBe(text);
    }

    [Fact]
    public void ADoubledOpeningBraceIsALiteralOne()
    {
        Placeholders.Scan("{{sha}").ShouldBeEmpty();
        Placeholders.Expand("{{sha}", AllValues()).ShouldBe("{sha}");
        Placeholders.Expand("{{{sha7}", AllValues()).ShouldBe("{" + AllValues().Sha!.Substring(0, 7));
    }

    [Fact]
    public void ANameInBracesThatIsNotAPlaceholderIsStillFoundSoThatItCanBeRefused()
    {
        Placeholders.Scan("${HOME}").Select(t => t.Name).ShouldBeSequence("HOME");
        Placeholders.Scan("{Sha}").Select(t => t.Name).ShouldBeSequence("Sha");
        Placeholders.Scan("{env:PATH}").ShouldHaveSingleItem().Argument.ShouldBe("PATH");
    }

    [Fact]
    public void ExpandingWithoutAValueIsAnError()
    {
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{worktree}", new PlaceholderValues { Sha = "abc" }));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{sha}", new PlaceholderValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{round}", new PlaceholderValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{bind:ghost}", AllValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{bind:data}", new PlaceholderValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{bind:}", AllValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{sha:x}", AllValues()));
        Should.Throw<PlaceholderException>(() => Placeholders.Expand("{nope}", AllValues()));
    }

    [Fact]
    public void ShortShaStillGivesAShortIdentifier()
    {
        var values = new PlaceholderValues { Sha = "abc" };

        Placeholders.Expand("{sha7}", values).ShouldBe("abc");
    }

    [Fact]
    public void SubstituteReplacesEachPlaceholderAndResolvesTheEscape()
    {
        var result = Placeholders.Substitute("a {sha} b {{x} {bind:q}", token => "<" + token.Name + ">");

        result.ShouldBe("a <sha> b {x} <bind>");
    }

    [Fact]
    public void EveryDocumentedPlaceholderIsKnownToTheValidator()
    {
        Placeholders.Names.ShouldBeSequence("sha", "sha7", "label", "worktree", "runDir", "artifactDir", "round", "task", "step");
        var edit = TestRequests.Edit(TestRequests.Minimal);
        TestRequests.StepAt(edit, 0)["command"] =
            "echo " + string.Join(" ", Placeholders.Names.Select(name => "{" + name + "}"));

        TestRequests.Parse(edit).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void PlaceholdersAreCheckedInEveryFieldThatAllowsThem()
    {
        var fields = new[]
        {
            new FieldCase("steps[1].command", r => TestRequests.StepAt(r, 1)["command"] = "x {nope}"),
            new FieldCase("steps[1].workdir", r => TestRequests.StepAt(r, 1)["workdir"] = "src\\{nope}"),
            new FieldCase("steps[1].env.CONFIG", r => TestRequests.StepAt(r, 1)["env"]!["CONFIG"] = "{nope}"),
            new FieldCase("steps[1].env.API_TOKEN.value", r => TestRequests.StepAt(r, 1)["env"]!["API_TOKEN"]!["value"] = "{nope}"),
            new FieldCase("steps[2].expect[0].in", r => TestRequests.CheckAt(r, 2, 0)["in"] = "artifact:{nope}"),
            new FieldCase("steps[2].expect[0].mustMatch", r => TestRequests.CheckAt(r, 2, 0)["mustMatch"] = "{nope}"),
            new FieldCase("steps[2].expect[0].message", r => TestRequests.CheckAt(r, 2, 0)["message"] = "{nope}"),
            new FieldCase("steps[2].expect[1].mustNotMatch", r => TestRequests.CheckAt(r, 2, 1)["mustNotMatch"] = "{nope}"),
            new FieldCase("steps[2].expect[2].mustContain", r => TestRequests.CheckAt(r, 2, 2)["mustContain"] = "{nope}"),
            new FieldCase("steps[2].artifacts[0]", r => TestRequests.StepAt(r, 2)["artifacts"]![0] = "{nope}"),
            new FieldCase("steps[2].artifacts[1].glob", r => TestRequests.StepAt(r, 2)["artifacts"]![1]!["glob"] = "{nope}"),
            new FieldCase("steps[0].inputs[0]", r => TestRequests.StepAt(r, 0)["inputs"]![0] = "{nope}"),
        };

        foreach (var field in fields)
        {
            var path = field.Path;
            var root = TestRequests.Edit(TestRequests.Complete);
            field.Edit(root);

            var result = TestRequests.Parse(root);

            result.Issues.Any(i => i.Code == "unknown-placeholder" && i.Path == path)
                .ShouldBeTrue("No unknown-placeholder at " + path + ": " + string.Join("; ", result.Issues.Select(i => i.ToString())));
        }
    }

    [Fact]
    public void ABindingUsedInAnyFieldCountsAsUsed()
    {
        var root = TestRequests.Edit(TestRequests.Complete);
        TestRequests.StepAt(root, 0)["inputs"]![0] = "{bind:profile}";
        TestRequests.StepAt(root, 1)["env"]!["CONFIG"] = "{bind:note}";
        TestRequests.StepAt(root, 2)["artifacts"]![0] = "{bind:bare}\\*.json";

        var result = TestRequests.Parse(root);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }
}
