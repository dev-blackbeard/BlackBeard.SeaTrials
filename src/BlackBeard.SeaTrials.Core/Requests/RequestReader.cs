using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>
/// Reads a request file: UTF-8 JSON, comments and trailing commas tolerated. This is the structural pass (shape,
/// types, unknown and duplicate keys); <see cref="RequestValidator"/> is the semantic pass. Both run in <c>Parse</c>.
/// </summary>
public static class RequestReader
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = text.TrimStart('﻿');
        if (string.IsNullOrWhiteSpace(content))
        {
            return ParseResult.Failed("empty-file", "The file is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, DocumentOptions);
        }
        catch (JsonException error)
        {
            var where = error.LineNumber is long line
                ? " (line " + (line + 1).ToString(CultureInfo.InvariantCulture) + ")"
                : "";
            return ParseResult.Failed("json-syntax", "The file is not valid JSON" + where + ".");
        }

        using (document)
        {
            var walker = new Walker();
            var file = walker.ReadRoot(document.RootElement);
            var issues = new List<ValidationIssue>(walker.Issues);
            if (file is null || issues.Count > 0)
            {
                return new ParseResult(null, issues);
            }

            issues.AddRange(RequestValidator.Validate(file));
            return new ParseResult(file, issues);
        }
    }

    public static ParseResult ParseBytes(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return ParseResult.Failed("invalid-utf8", "The file is not valid UTF-8.");
        }

        return Parse(text);
    }

    private sealed class Walker
    {
        public List<ValidationIssue> Issues { get; } = new();

        public RequestFile? ReadRoot(JsonElement root)
        {
            var map = Obj(root, "", RequestKeys.Root);
            if (map is null)
            {
                return null;
            }

            var format = Str(map, "format", "", true);
            var name = Str(map, "name", "", true);
            var purpose = Str(map, "purpose", "", false);
            var repo = ReadRepo(map);
            var bindings = ReadBindingList(map);
            var revisions = List(map, "revisions", "", ReadRevision, required: true);
            var steps = List(map, "steps", "", ReadStep);
            var tasks = List(map, "tasks", "", ReadTask);
            var matrix = ReadMatrix(map);
            var compare = List(map, "compare", "", ReadCompare);
            var generated = ReadGenerated(map);

            return new RequestFile(
                format ?? "",
                name ?? "",
                purpose,
                repo,
                bindings,
                revisions ?? new List<Revision>(),
                steps,
                tasks,
                matrix,
                compare,
                generated);
        }

        private RepoRef ReadRepo(Dictionary<string, JsonElement> root)
        {
            if (!root.TryGetValue("repo", out var element))
            {
                Error("missing-key", "repo", "Missing required key \"repo\".");
                return new RepoRef("");
            }

            var map = Obj(element, "repo", RequestKeys.Repo);
            return map is null ? new RepoRef("") : new RepoRef(Str(map, "remote", "repo", true) ?? "");
        }

        private Revision ReadRevision(JsonElement element, string path)
        {
            var map = Obj(element, path, RequestKeys.Revision);
            if (map is null)
            {
                return new Revision("");
            }

            return new Revision(
                Str(map, "label", path, true) ?? "",
                Str(map, "rev", path, false),
                Str(map, "sha", path, false));
        }

        private Step ReadStep(JsonElement element, string path)
        {
            var map = Obj(element, path, RequestKeys.Step);
            if (map is null)
            {
                return new Step("", "");
            }

            return new Step(
                Name: Str(map, "name", path, true) ?? "",
                Command: Str(map, "command", path, true) ?? "",
                Kind: EnumValue(map, "kind", path, EnumNames.StepKinds),
                Workdir: Str(map, "workdir", path, false),
                Shell: EnumValue(map, "shell", path, EnumNames.Shells),
                Env: ReadEnv(map, path),
                TimeoutMinutes: Int(map, "timeoutMinutes", path),
                OnFailure: EnumValue(map, "onFailure", path, EnumNames.FailureActions),
                OkExitCodes: ReadExitCodes(map, path),
                HandsOff: Bool(map, "handsOff", path),
                Expect: List(map, "expect", path, ReadCheck),
                Artifacts: List(map, "artifacts", path, ReadArtifact),
                Inputs: List(map, "inputs", path, ReadInput));
        }

        private List<int>? ReadExitCodes(Dictionary<string, JsonElement> map, string path)
        {
            return List(map, "okExitCodes", path, (element, itemPath) =>
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    WrongType(itemPath, "a whole number", element);
                    return 0;
                }

                if (!element.TryGetInt32(out var value))
                {
                    Error("not-integer", itemPath, "Must be a whole number that fits in 32 bits.");
                    return 0;
                }

                return value;
            });
        }

        private List<EnvVar>? ReadEnv(Dictionary<string, JsonElement> map, string path)
        {
            if (!map.TryGetValue("env", out var element))
            {
                return null;
            }

            var at = Join(path, "env");
            if (element.ValueKind != JsonValueKind.Object)
            {
                WrongType(at, "an object", element);
                return null;
            }

            var list = new List<EnvVar>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var itemPath = Join(at, property.Name);
                if (!seen.Add(property.Name))
                {
                    Error("duplicate-key", itemPath, "Duplicate key \"" + property.Name + "\".");
                    continue;
                }

                var value = property.Value;
                if (value.ValueKind == JsonValueKind.String)
                {
                    list.Add(new EnvVar(property.Name, value.GetString() ?? ""));
                }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    var inner = Obj(value, itemPath, RequestKeys.EnvObject);
                    if (inner is not null)
                    {
                        list.Add(new EnvVar(
                            property.Name,
                            Str(inner, "value", itemPath, true) ?? "",
                            Bool(inner, "secret", itemPath),
                            ObjectForm: true));
                    }
                }
                else
                {
                    WrongType(itemPath, "a string or an object", value);
                }
            }

            return list;
        }

        private Check ReadCheck(JsonElement element, string path)
        {
            var map = Obj(element, path, RequestKeys.Check);
            if (map is null)
            {
                return new Check("");
            }

            return new Check(
                Str(map, "in", path, true) ?? "",
                Str(map, "mustContain", path, false),
                Str(map, "mustMatch", path, false),
                Str(map, "mustNotMatch", path, false),
                Str(map, "message", path, false));
        }

        private ArtifactSpec ReadArtifact(JsonElement element, string path)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return new ArtifactSpec(element.GetString() ?? "");
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                WrongType(path, "a string or an object", element);
                return new ArtifactSpec("");
            }

            var map = Obj(element, path, RequestKeys.Artifact);
            if (map is null)
            {
                return new ArtifactSpec("");
            }

            return new ArtifactSpec(
                Str(map, "glob", path, true) ?? "",
                Bool(map, "optional", path),
                Bool(map, "includeOlder", path),
                ObjectForm: true);
        }

        private string ReadInput(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                WrongType(path, "a string", element);
                return "";
            }

            return element.GetString() ?? "";
        }

        private TaskSpec ReadTask(JsonElement element, string path)
        {
            var map = Obj(element, path, RequestKeys.Task);
            if (map is null)
            {
                return new TaskSpec("", "", new List<Step>());
            }

            var steps = List(map, "steps", path, ReadStep, required: true);
            return new TaskSpec(
                Str(map, "name", path, true) ?? "",
                Str(map, "revision", path, true) ?? "",
                steps ?? new List<Step>());
        }

        private Matrix? ReadMatrix(Dictionary<string, JsonElement> root)
        {
            if (!root.TryGetValue("matrix", out var element))
            {
                return null;
            }

            var map = Obj(element, "matrix", RequestKeys.Matrix);
            if (map is null)
            {
                return null;
            }

            return new Matrix(
                Int(map, "rounds", "matrix"),
                EnumValue(map, "order", "matrix", EnumNames.MatrixOrders),
                Int(map, "pauseBetweenSeconds", "matrix"));
        }

        private ComparePair ReadCompare(JsonElement element, string path)
        {
            var map = Obj(element, path, RequestKeys.Compare);
            if (map is null)
            {
                return new ComparePair("", "", "");
            }

            return new ComparePair(
                Str(map, "name", path, true) ?? "",
                Str(map, "a", path, true) ?? "",
                Str(map, "b", path, true) ?? "");
        }

        private Generated? ReadGenerated(Dictionary<string, JsonElement> root)
        {
            if (!root.TryGetValue("generated", out var element))
            {
                return null;
            }

            var map = Obj(element, "generated", RequestKeys.Generated);
            if (map is null)
            {
                return null;
            }

            return new Generated(
                Str(map, "by", "generated", true) ?? "",
                Str(map, "at", "generated", true) ?? "",
                Str(map, "tool", "generated", true) ?? "",
                Str(map, "baselineSha256", "generated", true) ?? "",
                Str(map, "baseline", "generated", true) ?? "");
        }

        // Bindings are an object of objects: each value is read in order and named by its key.
        private List<BindingSpec>? ReadBindingList(Dictionary<string, JsonElement> root)
        {
            if (!root.TryGetValue("bindings", out var element))
            {
                return null;
            }

            if (element.ValueKind != JsonValueKind.Object)
            {
                WrongType("bindings", "an object", element);
                return null;
            }

            var list = new List<BindingSpec>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var itemPath = Join("bindings", property.Name);
                if (!seen.Add(property.Name))
                {
                    Error("duplicate-key", itemPath, "Duplicate key \"" + property.Name + "\".");
                    continue;
                }

                var map = Obj(property.Value, itemPath, RequestKeys.Binding);
                list.Add(map is null
                    ? new BindingSpec(property.Name)
                    : new BindingSpec(
                        property.Name,
                        Str(map, "description", itemPath, false),
                        EnumValue(map, "kind", itemPath, EnumNames.BindingKinds)));
            }

            return list;
        }

        // ---- helpers -------------------------------------------------------------------------------------

        private void Error(string code, string path, string message) =>
            Issues.Add(new ValidationIssue(Severity.Error, code, path, message));

        private static string Join(string path, string key) => path.Length == 0 ? key : path + "." + key;

        private static string Index(string path, int index) =>
            path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        private static string Describe(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Object => "an object",
            JsonValueKind.Array => "an array",
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Null => "null",
            _ => "something else",
        };

        private void WrongType(string path, string expected, JsonElement found) =>
            Error("wrong-type", path, "Expected " + expected + " but found " + Describe(found.ValueKind) + ".");

        // An object's properties, with unknown and duplicate keys reported. Null when it is not an object.
        private Dictionary<string, JsonElement>? Obj(JsonElement element, string path, IReadOnlySet<string> known)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                WrongType(path, "an object", element);
                return null;
            }

            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var at = Join(path, property.Name);
                if (!known.Contains(property.Name))
                {
                    Error("unknown-key", at, "Unknown key \"" + property.Name + "\".");
                }
                else if (!map.TryAdd(property.Name, property.Value))
                {
                    Error("duplicate-key", at, "Duplicate key \"" + property.Name + "\".");
                }
            }

            return map;
        }

        private string? Str(Dictionary<string, JsonElement> map, string key, string path, bool required)
        {
            if (!map.TryGetValue(key, out var element))
            {
                if (required)
                {
                    Error("missing-key", Join(path, key), "Missing required key \"" + key + "\".");
                }

                return null;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                WrongType(Join(path, key), "a string", element);
                return null;
            }

            return element.GetString();
        }

        private int? Int(Dictionary<string, JsonElement> map, string key, string path)
        {
            if (!map.TryGetValue(key, out var element))
            {
                return null;
            }

            var at = Join(path, key);
            if (element.ValueKind != JsonValueKind.Number)
            {
                WrongType(at, "a whole number", element);
                return null;
            }

            if (!element.TryGetInt32(out var value))
            {
                Error("not-integer", at, "Must be a whole number that fits in 32 bits.");
                return null;
            }

            return value;
        }

        private bool? Bool(Dictionary<string, JsonElement> map, string key, string path)
        {
            if (!map.TryGetValue(key, out var element))
            {
                return null;
            }

            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return element.GetBoolean();
            }

            WrongType(Join(path, key), "a boolean", element);
            return null;
        }

        private T? EnumValue<T>(Dictionary<string, JsonElement> map, string key, string path, IReadOnlyDictionary<string, T> names)
            where T : struct
        {
            var text = Str(map, key, path, false);
            if (text is null)
            {
                return null;
            }

            if (names.TryGetValue(text, out var value))
            {
                return value;
            }

            Error("bad-value", Join(path, key), "Must be one of: " + string.Join(", ", names.Keys.Select(k => "\"" + k + "\"")) + ".");
            return null;
        }

        // An array, each item read in order and given its path. Null when the key is absent or not an array.
        private List<T>? List<T>(
            Dictionary<string, JsonElement> map,
            string key,
            string path,
            Func<JsonElement, string, T> read,
            bool required = false)
        {
            if (!map.TryGetValue(key, out var element))
            {
                if (required)
                {
                    Error("missing-key", Join(path, key), "Missing required key \"" + key + "\".");
                }

                return null;
            }

            var at = Join(path, key);
            if (element.ValueKind != JsonValueKind.Array)
            {
                WrongType(at, "an array", element);
                return null;
            }

            var list = new List<T>();
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                list.Add(read(item, Index(at, index)));
                index++;
            }

            return list;
        }
    }
}
