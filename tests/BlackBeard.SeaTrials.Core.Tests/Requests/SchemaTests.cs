using System.Text.Json;
using BlackBeard.SeaTrials.Core.Requests;

namespace BlackBeard.SeaTrials.Core.Tests.Requests;

/// <summary>The JSON schema and the reader must know the same keys.</summary>
public class SchemaTests
{
    private static JsonElement Schema()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "schemas", "seatrials-1.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string[] KeysOf(JsonElement objectSchema) =>
        objectSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(IReadOnlySet<string> keys) => keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [Fact]
    public void TheSchemaListsTheKeysTheReaderKnows()
    {
        var schema = Schema();
        var defs = schema.GetProperty("$defs");

        KeysOf(schema).ShouldBeSequence(Sorted(RequestKeys.Root));
        KeysOf(defs.GetProperty("repo")).ShouldBeSequence(Sorted(RequestKeys.Repo));
        KeysOf(defs.GetProperty("binding")).ShouldBeSequence(Sorted(RequestKeys.Binding));
        KeysOf(defs.GetProperty("revision")).ShouldBeSequence(Sorted(RequestKeys.Revision));
        KeysOf(defs.GetProperty("step")).ShouldBeSequence(Sorted(RequestKeys.Step));
        KeysOf(defs.GetProperty("check")).ShouldBeSequence(Sorted(RequestKeys.Check));
        KeysOf(defs.GetProperty("artifact")).ShouldBeSequence(Sorted(RequestKeys.Artifact));
        KeysOf(defs.GetProperty("task")).ShouldBeSequence(Sorted(RequestKeys.Task));
        KeysOf(defs.GetProperty("matrix")).ShouldBeSequence(Sorted(RequestKeys.Matrix));
        KeysOf(defs.GetProperty("compare")).ShouldBeSequence(Sorted(RequestKeys.Compare));
        KeysOf(defs.GetProperty("generated")).ShouldBeSequence(Sorted(RequestKeys.Generated));

        var envObject = defs.GetProperty("step").GetProperty("properties").GetProperty("env")
            .GetProperty("additionalProperties").GetProperty("oneOf")[1];
        KeysOf(envObject).ShouldBeSequence(Sorted(RequestKeys.EnvObject));
    }

    [Fact]
    public void TheSchemaRequiresWhatTheReaderRequires()
    {
        var schema = Schema();
        var defs = schema.GetProperty("$defs");

        Required(schema).ShouldBeSequence("format", "name", "repo", "revisions");
        Required(defs.GetProperty("repo")).ShouldBeSequence("remote");
        Required(defs.GetProperty("revision")).ShouldBeSequence("label");
        Required(defs.GetProperty("step")).ShouldBeSequence("command", "name");
        Required(defs.GetProperty("check")).ShouldBeSequence("in");
        Required(defs.GetProperty("artifact")).ShouldBeSequence("glob");
        Required(defs.GetProperty("task")).ShouldBeSequence("name", "revision", "steps");
        Required(defs.GetProperty("compare")).ShouldBeSequence("a", "b", "name");
        Required(defs.GetProperty("generated")).ShouldBeSequence("at", "baseline", "baselineSha256", "by", "tool");
    }

    [Fact]
    public void TheSchemaListsTheSameEnumerations()
    {
        var step = Schema().GetProperty("$defs").GetProperty("step").GetProperty("properties");

        EnumValues(step.GetProperty("kind")).ShouldBeSequence("build", "other", "run", "test");
        EnumValues(step.GetProperty("shell")).ShouldBeSequence("cmd", "pwsh");
        EnumValues(step.GetProperty("onFailure")).ShouldBeSequence("continue", "stop");
        EnumValues(Schema().GetProperty("$defs").GetProperty("matrix").GetProperty("properties").GetProperty("order"))
            .ShouldBeSequence("blocked", "interleaved");
        EnumValues(Schema().GetProperty("$defs").GetProperty("binding").GetProperty("properties").GetProperty("kind"))
            .ShouldBeSequence("file", "folder", "text");
    }

    private static string[] Required(JsonElement objectSchema) =>
        objectSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString() ?? "").OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string[] EnumValues(JsonElement property) =>
        property.GetProperty("enum").EnumerateArray().Select(e => e.GetString() ?? "").OrderBy(n => n, StringComparer.Ordinal).ToArray();
}
