using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>
/// Writes a request as JSON: two-space indentation, LF line endings, keys in the documented order, nothing invented.
/// What <see cref="RequestReader"/> read is what is written back, as a JSON value tree.
/// </summary>
public static class RequestWriter
{
    public static string ToJson(RequestFile file) => ToJson(file, includeGenerated: true);

    /// <summary>With <c>includeGenerated</c> false the result is the plan: the whole file except the stamp.</summary>
    public static string ToJson(RequestFile file, bool includeGenerated)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            WriteFile(writer, file, includeGenerated);
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    private static void WriteFile(Utf8JsonWriter w, RequestFile file, bool includeGenerated)
    {
        w.WriteStartObject();
        w.WriteString("format", file.Format);
        w.WriteString("name", file.Name);
        Optional(w, "purpose", file.Purpose);
        w.WriteStartObject("repo");
        w.WriteString("remote", file.Repo.Remote);
        w.WriteEndObject();

        if (file.Bindings is { } bindings)
        {
            w.WriteStartObject("bindings");
            foreach (var binding in bindings)
            {
                w.WriteStartObject(binding.Name);
                Optional(w, "description", binding.Description);
                if (binding.Kind is { } kind)
                {
                    w.WriteString("kind", EnumNames.NameOf(EnumNames.BindingKinds, kind));
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteStartArray("revisions");
        foreach (var revision in file.Revisions)
        {
            w.WriteStartObject();
            w.WriteString("label", revision.Label);
            Optional(w, "rev", revision.Rev);
            Optional(w, "sha", revision.Sha);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        if (file.Steps is { } steps)
        {
            w.WriteStartArray("steps");
            foreach (var step in steps)
            {
                WriteStep(w, step);
            }

            w.WriteEndArray();
        }

        if (file.Tasks is { } tasks)
        {
            w.WriteStartArray("tasks");
            foreach (var task in tasks)
            {
                w.WriteStartObject();
                w.WriteString("name", task.Name);
                w.WriteString("revision", task.Revision);
                w.WriteStartArray("steps");
                foreach (var step in task.Steps)
                {
                    WriteStep(w, step);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (file.Matrix is { } matrix)
        {
            w.WriteStartObject("matrix");
            if (matrix.Rounds is { } rounds)
            {
                w.WriteNumber("rounds", rounds);
            }

            if (matrix.Order is { } order)
            {
                w.WriteString("order", EnumNames.NameOf(EnumNames.MatrixOrders, order));
            }

            if (matrix.PauseBetweenSeconds is { } pause)
            {
                w.WriteNumber("pauseBetweenSeconds", pause);
            }

            w.WriteEndObject();
        }

        if (file.Compare is { } compare)
        {
            w.WriteStartArray("compare");
            foreach (var pair in compare)
            {
                w.WriteStartObject();
                w.WriteString("name", pair.Name);
                w.WriteString("a", pair.A);
                w.WriteString("b", pair.B);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (includeGenerated && file.Generated is { } generated)
        {
            w.WriteStartObject("generated");
            w.WriteString("by", generated.By);
            w.WriteString("at", generated.At);
            w.WriteString("tool", generated.Tool);
            w.WriteString("baselineSha256", generated.BaselineSha256);
            w.WriteString("baseline", generated.Baseline);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter w, Step step)
    {
        w.WriteStartObject();
        w.WriteString("name", step.Name);
        if (step.Kind is { } kind)
        {
            w.WriteString("kind", EnumNames.NameOf(EnumNames.StepKinds, kind));
        }

        w.WriteString("command", step.Command);
        Optional(w, "workdir", step.Workdir);
        if (step.Shell is { } shell)
        {
            w.WriteString("shell", EnumNames.NameOf(EnumNames.Shells, shell));
        }

        if (step.Env is { } env)
        {
            w.WriteStartObject("env");
            foreach (var variable in env)
            {
                if (variable.ObjectForm || variable.Secret is not null)
                {
                    w.WriteStartObject(variable.Name);
                    w.WriteString("value", variable.Value);
                    if (variable.Secret is { } secret)
                    {
                        w.WriteBoolean("secret", secret);
                    }

                    w.WriteEndObject();
                }
                else
                {
                    w.WriteString(variable.Name, variable.Value);
                }
            }

            w.WriteEndObject();
        }

        if (step.TimeoutMinutes is { } timeout)
        {
            w.WriteNumber("timeoutMinutes", timeout);
        }

        if (step.OnFailure is { } onFailure)
        {
            w.WriteString("onFailure", EnumNames.NameOf(EnumNames.FailureActions, onFailure));
        }

        if (step.OkExitCodes is { } codes)
        {
            w.WriteStartArray("okExitCodes");
            foreach (var code in codes)
            {
                w.WriteNumberValue(code);
            }

            w.WriteEndArray();
        }

        if (step.HandsOff is { } handsOff)
        {
            w.WriteBoolean("handsOff", handsOff);
        }

        if (step.Expect is { } checks)
        {
            w.WriteStartArray("expect");
            foreach (var check in checks)
            {
                w.WriteStartObject();
                w.WriteString("in", check.In);
                Optional(w, "mustContain", check.MustContain);
                Optional(w, "mustMatch", check.MustMatch);
                Optional(w, "mustNotMatch", check.MustNotMatch);
                Optional(w, "message", check.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (step.Artifacts is { } artifacts)
        {
            w.WriteStartArray("artifacts");
            foreach (var artifact in artifacts)
            {
                if (artifact.ObjectForm || artifact.Optional is not null || artifact.IncludeOlder is not null)
                {
                    w.WriteStartObject();
                    w.WriteString("glob", artifact.Glob);
                    if (artifact.Optional is { } optional)
                    {
                        w.WriteBoolean("optional", optional);
                    }

                    if (artifact.IncludeOlder is { } includeOlder)
                    {
                        w.WriteBoolean("includeOlder", includeOlder);
                    }

                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStringValue(artifact.Glob);
                }
            }

            w.WriteEndArray();
        }

        if (step.Inputs is { } inputs)
        {
            w.WriteStartArray("inputs");
            foreach (var input in inputs)
            {
                w.WriteStringValue(input);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void Optional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }
}
