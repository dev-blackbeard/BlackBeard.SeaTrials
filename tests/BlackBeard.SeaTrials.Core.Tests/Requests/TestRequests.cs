using System.Text.Json.Nodes;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

/// <summary>Synthetic request documents and the small helpers the tests share.</summary>
internal static class TestRequests
{
    public const string Sha1 = "0000000000000000000000000000000000000001";
    public const string Sha2 = "0000000000000000000000000000000000000002";
    public const string Sha3 = "0000000000000000000000000000000000000003";

    /// <summary>Every key of the format, in the steps form (every revision runs the steps).</summary>
    public const string Complete = """
        {
          "format": "seatrials/1",
          "name": "Complete request",
          "purpose": "Exercise every key.",
          "repo": { "remote": "https://example.com/acme/sample-repo.git" },
          "bindings": {
            "data": { "description": "Folder of input data", "kind": "folder" },
            "profile": { "description": "Profile file", "kind": "file" },
            "note": { "kind": "text" },
            "bare": {}
          },
          "revisions": [
            { "label": "before", "rev": "1a2b3c4^", "sha": "0000000000000000000000000000000000000001" },
            { "label": "after", "rev": "1a2b3c4", "sha": "0000000000000000000000000000000000000002" },
            { "label": "pinned-later", "rev": "alpha/topic" },
            { "label": "by-id", "sha": "0000000000000000000000000000000000000003" }
          ],
          "steps": [
            {
              "name": "overlay",
              "kind": "other",
              "command": "git apply C:\\work\\change.patch",
              "inputs": ["C:\\work\\change.patch"]
            },
            {
              "name": "build",
              "kind": "build",
              "command": "dotnet build -c Release",
              "workdir": "src",
              "shell": "pwsh",
              "env": {
                "CONFIG": "Release",
                "API_TOKEN": { "value": "not-a-real-value", "secret": true }
              },
              "timeoutMinutes": 10,
              "onFailure": "continue",
              "okExitCodes": [0, 2]
            },
            {
              "name": "measure",
              "kind": "run",
              "handsOff": true,
              "command": "run.exe --out {artifactDir} --data {bind:data} --sha {sha}",
              "artifacts": [
                "{artifactDir}\\*.json",
                { "glob": "*.txt", "optional": true, "includeOlder": true }
              ],
              "expect": [
                { "in": "artifact:*.txt", "mustMatch": "Build: .*\\+{sha7}", "message": "the report is not from this commit" },
                { "in": "output", "mustNotMatch": "Unhandled exception" },
                { "in": "output", "mustContain": "Done" }
              ]
            }
          ],
          "matrix": { "rounds": 2, "order": "blocked", "pauseBetweenSeconds": 60 },
          "compare": [{ "name": "the change", "a": "before", "b": "after" }],
          "generated": {
            "by": "a test",
            "at": "2026-11-02T10:00:00Z",
            "tool": "stamp.py 1",
            "baselineSha256": "0000000000000000000000000000000000000000000000000000000000000000",
            "baseline": "{}"
          }
        }
        """;

    /// <summary>The explicit-tasks form. Revision C is never used.</summary>
    public const string Tasks = """
        {
          "format": "seatrials/1",
          "name": "Tasks request",
          "repo": { "remote": "https://example.com/acme/sample-repo.git" },
          "revisions": [
            { "label": "A", "sha": "0000000000000000000000000000000000000001" },
            { "label": "B", "sha": "0000000000000000000000000000000000000002" },
            { "label": "C", "sha": "0000000000000000000000000000000000000003" }
          ],
          "tasks": [
            { "name": "build only", "revision": "A", "steps": [{ "name": "build", "command": "make" }] },
            {
              "name": "full",
              "revision": "B",
              "steps": [
                { "name": "build", "command": "make" },
                { "name": "test", "kind": "test", "command": "make test" }
              ]
            }
          ]
        }
        """;

    /// <summary>Only the required keys.</summary>
    public const string Minimal = """
        {
          "format": "seatrials/1",
          "name": "Minimal",
          "repo": { "remote": "https://example.com/acme/sample-repo.git" },
          "revisions": [{ "label": "only", "rev": "main" }],
          "steps": [{ "name": "run", "command": "echo hello" }]
        }
        """;

    public static JsonObject Edit(string json) => JsonNode.Parse(json)!.AsObject();

    public static JsonObject StepAt(JsonObject root, int index) => root["steps"]!.AsArray()[index]!.AsObject();

    public static JsonObject TaskAt(JsonObject root, int index) => root["tasks"]!.AsArray()[index]!.AsObject();

    public static JsonObject RevisionAt(JsonObject root, int index) => root["revisions"]!.AsArray()[index]!.AsObject();

    public static JsonObject CheckAt(JsonObject root, int step, int index) =>
        StepAt(root, step)["expect"]!.AsArray()[index]!.AsObject();

    public static ParseResult Parse(JsonObject root) => RequestFile.Parse(root.ToJsonString());

    /// <summary>Parses a document that must be valid and returns the request.</summary>
    public static RequestFile Load(string json)
    {
        var result = RequestFile.Parse(json);
        result.IsValid.ShouldBeTrue(string.Join("; ", result.Errors.Select(e => e.ToString())));
        return result.File.ShouldNotBeNull();
    }
}

internal static class SequenceAssertions
{
    /// <summary>Compares element by element, in order, whatever the collection types are.</summary>
    public static void ShouldBeSequence<T>(this IEnumerable<T> actual, params T[] expected)
    {
        var items = actual.ToList();
        items.SequenceEqual(expected).ShouldBeTrue(
            "Expected [" + string.Join(", ", expected) + "] but was [" + string.Join(", ", items) + "].");
    }
}
