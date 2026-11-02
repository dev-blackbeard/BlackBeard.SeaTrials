using System.Text;
using System.Text.Json.Nodes;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

/// <summary>Every kind of file error, each with the place it is reported at.</summary>
public class RequestErrorTests
{
    private sealed record Case(string Code, string Path, Action<JsonObject> Edit);

    private static Case C(string code, string path, Action<JsonObject> edit) => new(code, path, edit);

    private static JsonObject Step(JsonObject root, int index) => TestRequests.StepAt(root, index);

    private static readonly Case[] StructuralCases =
    {
        C("unknown-key", "flavour", r => r["flavour"] = 1),
        C("unknown-key", "repo.branch", r => r["repo"]!["branch"] = "main"),
        C("unknown-key", "revisions[0].tag", r => TestRequests.RevisionAt(r, 0)["tag"] = "v1"),
        C("unknown-key", "bindings.data.type", r => r["bindings"]!["data"]!["type"] = "x"),
        C("unknown-key", "steps[0].colour", r => Step(r, 0)["colour"] = "red"),
        C("unknown-key", "steps[1].env.API_TOKEN.hidden", r => Step(r, 1)["env"]!["API_TOKEN"]!["hidden"] = true),
        C("unknown-key", "steps[2].expect[0].regex", r => TestRequests.CheckAt(r, 2, 0)["regex"] = "x"),
        C("unknown-key", "steps[2].artifacts[1].required", r => Step(r, 2)["artifacts"]![1]!["required"] = true),
        C("unknown-key", "matrix.mode", r => r["matrix"]!["mode"] = "x"),
        C("unknown-key", "compare[0].c", r => r["compare"]![0]!["c"] = "x"),
        C("unknown-key", "generated.extra", r => r["generated"]!["extra"] = "x"),

        C("missing-key", "format", r => r.Remove("format")),
        C("missing-key", "name", r => r.Remove("name")),
        C("missing-key", "repo", r => r.Remove("repo")),
        C("missing-key", "revisions", r => r.Remove("revisions")),
        C("missing-key", "repo.remote", r => r["repo"]!.AsObject().Remove("remote")),
        C("missing-key", "revisions[0].label", r => TestRequests.RevisionAt(r, 0).Remove("label")),
        C("missing-key", "steps[0].name", r => Step(r, 0).Remove("name")),
        C("missing-key", "steps[0].command", r => Step(r, 0).Remove("command")),
        C("missing-key", "steps[1].env.API_TOKEN.value", r => Step(r, 1)["env"]!["API_TOKEN"]!.AsObject().Remove("value")),
        C("missing-key", "steps[2].artifacts[1].glob", r => Step(r, 2)["artifacts"]![1]!.AsObject().Remove("glob")),
        C("missing-key", "steps[2].expect[0].in", r => TestRequests.CheckAt(r, 2, 0).Remove("in")),
        C("missing-key", "compare[0].a", r => r["compare"]![0]!.AsObject().Remove("a")),
        C("missing-key", "generated.baseline", r => r["generated"]!.AsObject().Remove("baseline")),

        C("wrong-type", "name", r => r["name"] = 5),
        C("wrong-type", "repo", r => r["repo"] = "x"),
        C("wrong-type", "bindings", r => r["bindings"] = new JsonArray()),
        C("wrong-type", "steps", r => r["steps"] = new JsonObject()),
        C("wrong-type", "steps[0]", r => r["steps"]![0] = 3),
        C("wrong-type", "steps[0].kind", r => Step(r, 0)["kind"] = 5),
        C("wrong-type", "steps[0].inputs[0]", r => Step(r, 0)["inputs"]![0] = 5),
        C("wrong-type", "steps[1].env.CONFIG", r => Step(r, 1)["env"]!["CONFIG"] = 5),
        C("wrong-type", "steps[1].timeoutMinutes", r => Step(r, 1)["timeoutMinutes"] = "ten"),
        C("wrong-type", "steps[1].okExitCodes[0]", r => Step(r, 1)["okExitCodes"]![0] = "0"),
        C("wrong-type", "steps[2].handsOff", r => Step(r, 2)["handsOff"] = "yes"),
        C("wrong-type", "steps[2].artifacts[0]", r => Step(r, 2)["artifacts"]![0] = 5),
        C("wrong-type", "revisions[0]", r => r["revisions"]![0] = "main"),
        C("wrong-type", "matrix", r => r["matrix"] = 1),
        C("wrong-type", "compare", r => r["compare"] = 3),
        C("wrong-type", "generated", r => r["generated"] = 1),

        C("not-integer", "matrix.rounds", r => r["matrix"]!["rounds"] = 2.5),
        C("not-integer", "matrix.pauseBetweenSeconds", r => r["matrix"]!["pauseBetweenSeconds"] = 9999999999L),
        C("not-integer", "steps[1].timeoutMinutes", r => Step(r, 1)["timeoutMinutes"] = 1.5),
        C("not-integer", "steps[1].okExitCodes[0]", r => Step(r, 1)["okExitCodes"]![0] = 0.5),

        C("bad-value", "steps[0].kind", r => Step(r, 0)["kind"] = "compile"),
        C("bad-value", "steps[1].shell", r => Step(r, 1)["shell"] = "bash"),
        C("bad-value", "steps[1].onFailure", r => Step(r, 1)["onFailure"] = "retry"),
        C("bad-value", "matrix.order", r => r["matrix"]!["order"] = "random"),
        C("bad-value", "bindings.data.kind", r => r["bindings"]!["data"]!["kind"] = "url"),
    };

    private static readonly Case[] SemanticCases =
    {
        C("unsupported-format", "format", r => r["format"] = "seatrials/2"),
        C("empty-value", "name", r => r["name"] = " "),
        C("empty-value", "repo.remote", r => r["repo"]!["remote"] = ""),
        C("empty-value", "revisions[0].rev", r => TestRequests.RevisionAt(r, 0)["rev"] = ""),
        C("empty-value", "steps[0].command", r => Step(r, 0)["command"] = " "),
        C("empty-value", "steps[0].inputs[0]", r => Step(r, 0)["inputs"]![0] = ""),
        C("empty-value", "steps[1].workdir", r => Step(r, 1)["workdir"] = ""),
        C("empty-value", "steps[2].artifacts[0]", r => Step(r, 2)["artifacts"]![0] = ""),
        C("empty-value", "steps[2].artifacts[1].glob", r => Step(r, 2)["artifacts"]![1]!["glob"] = ""),
        C("empty-value", "steps[2].expect[1].mustNotMatch", r => TestRequests.CheckAt(r, 2, 1)["mustNotMatch"] = ""),
        C("empty-value", "compare[0].name", r => r["compare"]![0]!["name"] = ""),

        C("bad-remote", "repo.remote", r => r["repo"]!["remote"] = "-oProxyCommand=x"),
        C("bad-remote", "repo.remote", r => r["repo"]!["remote"] = "https://example.com/a b.git"),

        C("bad-name", "revisions[0].label", r => TestRequests.RevisionAt(r, 0)["label"] = "a/b"),
        C("bad-name", "revisions[0].label", r => TestRequests.RevisionAt(r, 0)["label"] = "ends with a dot."),
        C("bad-name", "revisions[0].label", r => TestRequests.RevisionAt(r, 0)["label"] = " leading space"),
        C("bad-name", "revisions[0].label", r => TestRequests.RevisionAt(r, 0)["label"] = new string('x', 81)),
        C("bad-name", "steps[0].name", r => Step(r, 0)["name"] = "a:b"),
        C("bad-name", "bindings.-x", r => r["bindings"]!["-x"] = new JsonObject()),

        C("duplicate-name", "revisions[1].label", r => TestRequests.RevisionAt(r, 1)["label"] = "before"),
        C("duplicate-name", "steps[1].name", r => Step(r, 1)["name"] = "overlay"),

        C("bad-sha", "revisions[0].sha", r => TestRequests.RevisionAt(r, 0)["sha"] = new string('A', 40)),
        C("bad-sha", "revisions[0].sha", r => TestRequests.RevisionAt(r, 0)["sha"] = "abc1234"),
        C("revision-needs-rev-or-sha", "revisions[2]", r => TestRequests.RevisionAt(r, 2).Remove("rev")),
        C("bad-rev", "revisions[0].rev", r => TestRequests.RevisionAt(r, 0)["rev"] = "--upload-pack=x"),
        C("no-revisions", "revisions", r => r["revisions"] = new JsonArray()),

        C("steps-and-tasks", "tasks", r => r["tasks"] = new JsonArray()),
        C("no-steps", "steps", r => r["steps"] = new JsonArray()),
        C("no-steps", "steps", r => r.Remove("steps")),

        C("out-of-range", "steps[1].timeoutMinutes", r => Step(r, 1)["timeoutMinutes"] = 0),
        C("out-of-range", "steps[1].timeoutMinutes", r => Step(r, 1)["timeoutMinutes"] = 10081),
        C("out-of-range", "matrix.rounds", r => r["matrix"]!["rounds"] = 0),
        C("out-of-range", "matrix.rounds", r => r["matrix"]!["rounds"] = 1001),
        C("out-of-range", "matrix.pauseBetweenSeconds", r => r["matrix"]!["pauseBetweenSeconds"] = -1),
        C("empty-list", "steps[1].okExitCodes", r => Step(r, 1)["okExitCodes"] = new JsonArray()),

        C("bad-workdir", "steps[1].workdir", r => Step(r, 1)["workdir"] = "/abs"),
        C("bad-workdir", "steps[1].workdir", r => Step(r, 1)["workdir"] = "C:\\abs"),
        C("bad-workdir", "steps[1].workdir", r => Step(r, 1)["workdir"] = "..\\up"),
        C("bad-workdir", "steps[1].workdir", r => Step(r, 1)["workdir"] = "a/../b"),
        C("bad-env-name", "steps[1].env.1X", r => Step(r, 1)["env"]!["1X"] = "v"),
        C("bad-env-name", "steps[1].env.A-B", r => Step(r, 1)["env"]!["A-B"] = "v"),

        C("bad-check", "steps[2].expect[0]", r => TestRequests.CheckAt(r, 2, 0).Remove("mustMatch")),
        C("bad-check", "steps[2].expect[1]", r => TestRequests.CheckAt(r, 2, 1)["mustContain"] = "also"),
        C("bad-check-target", "steps[2].expect[1].in", r => TestRequests.CheckAt(r, 2, 1)["in"] = "file"),
        C("bad-check-target", "steps[2].expect[1].in", r => TestRequests.CheckAt(r, 2, 1)["in"] = "artifact:"),
        C("bad-regex", "steps[2].expect[0].mustMatch", r => TestRequests.CheckAt(r, 2, 0)["mustMatch"] = "(unclosed"),
        C("bad-regex", "steps[2].expect[1].mustNotMatch", r => TestRequests.CheckAt(r, 2, 1)["mustNotMatch"] = "["),

        C("unknown-placeholder", "steps[1].command", r => Step(r, 1)["command"] = "echo {nope}"),
        C("unknown-placeholder", "steps[1].command", r => Step(r, 1)["command"] = "echo {Sha}"),
        C("unknown-placeholder", "steps[1].command", r => Step(r, 1)["command"] = "echo ${HOME}"),
        C("bad-placeholder", "steps[1].command", r => Step(r, 1)["command"] = "echo {sha:abc}"),
        C("bad-placeholder", "steps[1].command", r => Step(r, 1)["command"] = "echo {bind:}"),
        C("unknown-binding", "steps[1].command", r => Step(r, 1)["command"] = "echo {bind:ghost}"),

        C("unknown-revision", "compare[0].a", r => r["compare"]![0]!["a"] = "ghost"),
        C("unknown-revision", "compare[0].b", r => r["compare"]![0]!["b"] = "ghost"),
        C("same-revision", "compare[0]", r => r["compare"]![0]!["b"] = "before"),
    };

    private static readonly Case[] TaskCases =
    {
        C("unknown-key", "tasks[0].colour", r => TestRequests.TaskAt(r, 0)["colour"] = "red"),
        C("missing-key", "tasks[0].name", r => TestRequests.TaskAt(r, 0).Remove("name")),
        C("missing-key", "tasks[0].revision", r => TestRequests.TaskAt(r, 0).Remove("revision")),
        C("missing-key", "tasks[0].steps", r => TestRequests.TaskAt(r, 0).Remove("steps")),
        C("wrong-type", "tasks", r => r["tasks"] = "x"),
        C("unknown-revision", "tasks[0].revision", r => TestRequests.TaskAt(r, 0)["revision"] = "ghost"),
        C("duplicate-name", "tasks[1].name", r => TestRequests.TaskAt(r, 1)["name"] = "build only"),
        C("bad-name", "tasks[0].name", r => TestRequests.TaskAt(r, 0)["name"] = "a|b"),
        C("no-steps", "tasks[0].steps", r => TestRequests.TaskAt(r, 0)["steps"] = new JsonArray()),
        C("no-steps", "tasks", r => r["tasks"] = new JsonArray()),
        C("steps-and-tasks", "tasks", r => r["steps"] = new JsonArray(new JsonObject { ["name"] = "x", ["command"] = "y" })),
        C("empty-value", "tasks[1].steps[0].command", r => TestRequests.TaskAt(r, 1)["steps"]![0]!["command"] = ""),
    };

    [Fact]
    public void EveryStructuralErrorIsReportedWhereItHappened()
    {
        foreach (var testCase in StructuralCases)
        {
            ExpectIssue(TestRequests.Complete, testCase);
        }
    }

    [Fact]
    public void EverySemanticErrorIsReportedWhereItHappened()
    {
        foreach (var testCase in SemanticCases)
        {
            ExpectIssue(TestRequests.Complete, testCase);
        }
    }

    [Fact]
    public void EveryErrorOfTheTasksFormIsReportedWhereItHappened()
    {
        foreach (var testCase in TaskCases)
        {
            ExpectIssue(TestRequests.Tasks, testCase);
        }
    }

    [Fact]
    public void EveryErrorCodeHasATest()
    {
        var expected = new[]
        {
            "empty-file", "invalid-utf8", "json-syntax", "wrong-type", "unknown-key", "duplicate-key", "missing-key",
            "not-integer", "bad-value", "unsupported-format", "empty-value", "bad-name", "duplicate-name",
            "unknown-revision", "bad-sha", "revision-needs-rev-or-sha", "bad-rev", "bad-remote", "no-steps",
            "steps-and-tasks", "no-revisions", "out-of-range", "empty-list", "bad-workdir", "bad-env-name",
            "bad-check", "bad-check-target", "bad-regex", "unknown-placeholder", "bad-placeholder", "unknown-binding",
            "same-revision",
        };
        var covered = StructuralCases.Concat(SemanticCases).Concat(TaskCases).Select(c => c.Code)
            .Concat(new[] { "empty-file", "invalid-utf8", "json-syntax", "duplicate-key" })
            .Distinct()
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        covered.ShouldBeSequence(expected.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void AnEmptyFileIsReported(string text)
    {
        var result = RequestFile.Parse(text);

        result.File.ShouldBeNull();
        result.Errors.Select(e => e.Code).ShouldBeSequence("empty-file");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": ")]
    [InlineData("{ \"format\" \"seatrials/1\" }")]
    public void InvalidJsonIsReported(string text)
    {
        var result = RequestFile.Parse(text);

        result.File.ShouldBeNull();
        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => e.Code).ShouldBeSequence("json-syntax");
    }

    [Fact]
    public void InvalidUtf8IsReported()
    {
        var result = RequestFile.ParseBytes(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D });

        result.File.ShouldBeNull();
        result.Errors.Select(e => e.Code).ShouldBeSequence("invalid-utf8");
    }

    [Fact]
    public void ARootThatIsNotAnObjectIsReported()
    {
        var result = RequestFile.Parse("[1, 2]");

        result.File.ShouldBeNull();
        result.Issues.Any(i => i.Code == "wrong-type" && i.Path == "").ShouldBeTrue();
    }

    [Fact]
    public void DuplicateKeysAreReportedAndNeverQuietlyResolved()
    {
        const string json = """
            {
              "format": "seatrials/1",
              "name": "first",
              "name": "second",
              "repo": { "remote": "https://example.com/acme/sample-repo.git" },
              "bindings": { "data": {}, "data": {} },
              "revisions": [{ "label": "only", "rev": "main" }],
              "steps": [{ "name": "run", "command": "x", "env": { "A": "1", "A": "2" } }]
            }
            """;

        var result = RequestFile.Parse(json);

        result.File.ShouldBeNull();
        result.Issues.Any(i => i.Code == "duplicate-key" && i.Path == "name").ShouldBeTrue();
        result.Issues.Any(i => i.Code == "duplicate-key" && i.Path == "bindings.data").ShouldBeTrue();
        result.Issues.Any(i => i.Code == "duplicate-key" && i.Path == "steps[0].env.A").ShouldBeTrue();
    }

    [Fact]
    public void AFileWithSemanticErrorsStillYieldsAModelForTheEditor()
    {
        var edit = TestRequests.Edit(TestRequests.Minimal);
        TestRequests.StepAt(edit, 0)["command"] = "";

        var result = TestRequests.Parse(edit);

        result.IsValid.ShouldBeFalse();
        result.File.ShouldNotBeNull();
    }

    [Fact]
    public void WarningsDoNotMakeAFileInvalid()
    {
        var complete = RequestFile.Parse(TestRequests.Complete);
        complete.IsValid.ShouldBeTrue();
        complete.Warnings.Select(w => w.Path).ShouldBeSequence("bindings.profile", "bindings.note", "bindings.bare");
        complete.Warnings.All(w => w.Code == "unused-binding").ShouldBeTrue();

        var tasks = RequestFile.Parse(TestRequests.Tasks);
        tasks.IsValid.ShouldBeTrue();
        var warning = tasks.Warnings.ShouldHaveSingleItem();
        warning.Code.ShouldBe("unused-revision");
        warning.Path.ShouldBe("revisions[2]");
    }

    [Fact]
    public void AnIssueReadsAsOneLine()
    {
        var issue = new ValidationIssue(Severity.Error, "bad-sha", "revisions[0].sha", "Not a full commit id.");

        issue.ToString().ShouldBe("error bad-sha at revisions[0].sha: Not a full commit id.");
    }

    private static void ExpectIssue(string baseJson, Case testCase)
    {
        var root = TestRequests.Edit(baseJson);
        testCase.Edit(root);

        var result = TestRequests.Parse(root);

        var found = result.Issues.Any(i => i.IsError && i.Code == testCase.Code && i.Path == testCase.Path);
        found.ShouldBeTrue(
            "Expected error " + testCase.Code + " at " + testCase.Path + " but got: "
            + string.Join("; ", result.Issues.Select(i => i.ToString())));
        result.IsValid.ShouldBeFalse();
    }
}
