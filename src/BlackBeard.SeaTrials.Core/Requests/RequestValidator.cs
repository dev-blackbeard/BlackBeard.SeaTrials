using System.Text.RegularExpressions;

namespace BlackBeard.SeaTrials.Core.Requests;

/// <summary>
/// The semantic pass over a request: everything that can be wrong with a file that is well-formed JSON of the right
/// shape. Usable on a model that was edited in memory, not only on a freshly parsed one.
/// </summary>
public static class RequestValidator
{
    private const int MaxNameLength = 80;
    private const string UnsafeNameCharacters = "\\/:*?\"<>|";

    private static readonly Regex BindingNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex EnvNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new(@"^[0-9a-f]{40}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ValidationIssue> Validate(RequestFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return new Run(file).Execute();
    }

    private sealed class Run
    {
        private readonly RequestFile _file;
        private readonly List<ValidationIssue> _issues = new();
        private readonly HashSet<string> _declaredBindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedBindings = new(StringComparer.Ordinal);
        private readonly HashSet<string> _labels = new(StringComparer.Ordinal);
        private readonly HashSet<string> _usedLabels = new(StringComparer.Ordinal);

        public Run(RequestFile file)
        {
            _file = file;
        }

        public IReadOnlyList<ValidationIssue> Execute()
        {
            if (_file.Format != RequestFile.FormatId)
            {
                Error("unsupported-format", "format", "The format must be \"" + RequestFile.FormatId + "\".");
            }

            if (IsBlank(_file.Name))
            {
                Error("empty-value", "name", "The name must not be empty.");
            }

            CheckRepo();
            CheckBindings();
            CheckRevisions();
            CheckStepsAndTasks();
            CheckMatrix();
            CheckCompare();
            CheckUnused();
            return _issues;
        }

        private void CheckRepo()
        {
            var remote = _file.Repo.Remote;
            if (IsBlank(remote))
            {
                Error("empty-value", "repo.remote", "The remote address must not be empty.");
            }
            else if (remote.StartsWith('-') || remote.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                Error("bad-remote", "repo.remote", "The remote address must not start with '-' or contain spaces or control characters.");
            }
        }

        private void CheckBindings()
        {
            foreach (var binding in _file.Bindings ?? Array.Empty<BindingSpec>())
            {
                var path = "bindings." + binding.Name;
                if (!BindingNamePattern.IsMatch(binding.Name))
                {
                    Error("bad-name", path, "A binding name uses letters, digits, '_', '-' and '.', and starts with a letter or digit.");
                }
                else if (!_declaredBindings.Add(binding.Name))
                {
                    Error("duplicate-name", path, "The binding \"" + binding.Name + "\" is declared twice.");
                }
            }
        }

        private void CheckRevisions()
        {
            if (_file.Revisions.Count == 0)
            {
                Error("no-revisions", "revisions", "At least one revision is required.");
            }

            for (var i = 0; i < _file.Revisions.Count; i++)
            {
                var revision = _file.Revisions[i];
                var path = "revisions[" + i + "]";
                CheckName(revision.Label, path + ".label", _labels, "revision label");
                if (revision.Rev is null && revision.Sha is null)
                {
                    Error("revision-needs-rev-or-sha", path, "A revision needs a \"rev\", a \"sha\", or both.");
                }

                if (revision.Sha is not null && !ShaPattern.IsMatch(revision.Sha))
                {
                    Error("bad-sha", path + ".sha", "The sha must be a full commit id: 40 lower-case hexadecimal digits.");
                }

                if (revision.Rev is not null)
                {
                    if (IsBlank(revision.Rev))
                    {
                        Error("empty-value", path + ".rev", "The rev must not be empty.");
                    }
                    else if (revision.Rev.StartsWith('-') || revision.Rev.Any(char.IsControl))
                    {
                        Error("bad-rev", path + ".rev", "The rev must not start with '-' or contain control characters.");
                    }
                }
            }
        }

        private void CheckStepsAndTasks()
        {
            if (_file.Steps is not null && _file.Tasks is not null)
            {
                Error("steps-and-tasks", "tasks", "Use either \"steps\" (every revision runs them) or \"tasks\", not both.");
            }
            else if (_file.Steps is null && _file.Tasks is null)
            {
                Error("no-steps", "steps", "The request needs \"steps\" or \"tasks\".");
            }

            if (_file.Steps is { } steps)
            {
                if (steps.Count == 0)
                {
                    Error("no-steps", "steps", "At least one step is required.");
                }

                CheckStepList(steps, "steps");
                foreach (var label in _labels)
                {
                    _usedLabels.Add(label);
                }
            }

            if (_file.Tasks is { } tasks)
            {
                if (tasks.Count == 0)
                {
                    Error("no-steps", "tasks", "At least one task is required.");
                }

                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < tasks.Count; i++)
                {
                    var task = tasks[i];
                    var path = "tasks[" + i + "]";
                    CheckName(task.Name, path + ".name", names, "task name");
                    if (_labels.Contains(task.Revision))
                    {
                        _usedLabels.Add(task.Revision);
                    }
                    else
                    {
                        Error("unknown-revision", path + ".revision", "No revision has the label \"" + task.Revision + "\".");
                    }

                    if (task.Steps.Count == 0)
                    {
                        Error("no-steps", path + ".steps", "A task needs at least one step.");
                    }

                    CheckStepList(task.Steps, path + ".steps");
                }
            }
        }

        private void CheckStepList(IReadOnlyList<Step> steps, string path)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < steps.Count; i++)
            {
                CheckStep(steps[i], path + "[" + i + "]", names);
            }
        }

        private void CheckStep(Step step, string path, HashSet<string> names)
        {
            CheckName(step.Name, path + ".name", names, "step name");
            if (IsBlank(step.Command))
            {
                Error("empty-value", path + ".command", "The command must not be empty.");
            }
            else
            {
                CheckText(step.Command, path + ".command");
            }

            if (step.Workdir is not null)
            {
                if (IsBlank(step.Workdir))
                {
                    Error("empty-value", path + ".workdir", "The working directory must not be empty.");
                }
                else
                {
                    CheckWorkdir(step.Workdir, path + ".workdir");
                    CheckText(step.Workdir, path + ".workdir");
                }
            }

            if (step.TimeoutMinutes is { } timeout && (timeout < 1 || timeout > 10080))
            {
                Error("out-of-range", path + ".timeoutMinutes", "The timeout must be between 1 and 10080 minutes.");
            }

            if (step.OkExitCodes is { Count: 0 })
            {
                Error("empty-list", path + ".okExitCodes", "okExitCodes must list at least one exit code.");
            }

            var envNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var variable in step.Env ?? Array.Empty<EnvVar>())
            {
                var envPath = path + ".env." + variable.Name;
                if (!EnvNamePattern.IsMatch(variable.Name))
                {
                    Error("bad-env-name", envPath, "An environment variable name uses letters, digits and '_', and does not start with a digit.");
                }
                else if (!envNames.Add(variable.Name))
                {
                    Error("duplicate-name", envPath, "The variable \"" + variable.Name + "\" is set twice.");
                }

                CheckText(variable.Value, variable.ObjectForm ? envPath + ".value" : envPath);
            }

            var checks = step.Expect ?? Array.Empty<Check>();
            for (var i = 0; i < checks.Count; i++)
            {
                CheckCheck(checks[i], path + ".expect[" + i + "]");
            }

            var artifacts = step.Artifacts ?? Array.Empty<ArtifactSpec>();
            for (var i = 0; i < artifacts.Count; i++)
            {
                var artifactPath = path + ".artifacts[" + i + "]" + (artifacts[i].ObjectForm ? ".glob" : "");
                if (IsBlank(artifacts[i].Glob))
                {
                    Error("empty-value", artifactPath, "The glob must not be empty.");
                }
                else
                {
                    CheckText(artifacts[i].Glob, artifactPath);
                }
            }

            var inputs = step.Inputs ?? Array.Empty<string>();
            for (var i = 0; i < inputs.Count; i++)
            {
                var inputPath = path + ".inputs[" + i + "]";
                if (IsBlank(inputs[i]))
                {
                    Error("empty-value", inputPath, "The input path must not be empty.");
                }
                else
                {
                    CheckText(inputs[i], inputPath);
                }
            }
        }

        private void CheckCheck(Check check, string path)
        {
            var conditions = new (string Key, string? Value)[]
            {
                ("mustContain", check.MustContain),
                ("mustMatch", check.MustMatch),
                ("mustNotMatch", check.MustNotMatch),
            };

            if (conditions.Count(c => c.Value is not null) != 1)
            {
                Error("bad-check", path, "A check needs exactly one of mustContain, mustMatch and mustNotMatch.");
            }

            if (check.InOutput)
            {
                // fine
            }
            else if (check.ArtifactGlob is { } glob && !IsBlank(glob))
            {
                CheckText(glob, path + ".in");
            }
            else
            {
                Error("bad-check-target", path + ".in", "\"in\" must be \"output\" or \"artifact:\" followed by a glob.");
            }

            foreach (var (key, value) in conditions)
            {
                if (value is null)
                {
                    continue;
                }

                if (value.Length == 0)
                {
                    Error("empty-value", path + "." + key, "The condition must not be empty.");
                    continue;
                }

                CheckText(value, path + "." + key);
                if (key != "mustContain")
                {
                    CheckRegex(value, path + "." + key);
                }
            }

            if (check.Message is not null)
            {
                CheckText(check.Message, path + ".message");
            }
        }

        private void CheckRegex(string pattern, string path)
        {
            var probe = Placeholders.Substitute(pattern, _ => "x");
            try
            {
                _ = new Regex(probe, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException)
            {
                Error("bad-regex", path, "Not a valid regular expression.");
            }
        }

        private void CheckWorkdir(string workdir, string path)
        {
            var text = workdir.Trim();
            var rooted = text.StartsWith('/') || text.StartsWith('\\') || (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':');
            var climbs = text.Split('/', '\\').Any(segment => segment == "..");
            if (rooted || climbs)
            {
                Error("bad-workdir", path, "The working directory must stay inside the worktree: relative, and without \"..\".");
            }
        }

        private void CheckMatrix()
        {
            if (_file.Matrix is not { } matrix)
            {
                return;
            }

            if (matrix.Rounds is { } rounds && (rounds < 1 || rounds > 1000))
            {
                Error("out-of-range", "matrix.rounds", "The number of rounds must be between 1 and 1000.");
            }

            if (matrix.PauseBetweenSeconds is { } pause && (pause < 0 || pause > 86400))
            {
                Error("out-of-range", "matrix.pauseBetweenSeconds", "The pause must be between 0 and 86400 seconds.");
            }
        }

        private void CheckCompare()
        {
            var pairs = _file.Compare ?? Array.Empty<ComparePair>();
            for (var i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                var path = "compare[" + i + "]";
                if (IsBlank(pair.Name))
                {
                    Error("empty-value", path + ".name", "The name must not be empty.");
                }

                var aKnown = _labels.Contains(pair.A);
                var bKnown = _labels.Contains(pair.B);
                if (!aKnown)
                {
                    Error("unknown-revision", path + ".a", "No revision has the label \"" + pair.A + "\".");
                }

                if (!bKnown)
                {
                    Error("unknown-revision", path + ".b", "No revision has the label \"" + pair.B + "\".");
                }

                if (aKnown && bKnown && pair.A == pair.B)
                {
                    Error("same-revision", path, "A comparison needs two different revisions.");
                }
            }
        }

        private void CheckUnused()
        {
            foreach (var binding in _file.Bindings ?? Array.Empty<BindingSpec>())
            {
                if (_declaredBindings.Contains(binding.Name) && !_usedBindings.Contains(binding.Name))
                {
                    Warn("unused-binding", "bindings." + binding.Name, "The binding \"" + binding.Name + "\" is declared but never used.");
                }
            }

            for (var i = 0; i < _file.Revisions.Count; i++)
            {
                if (_labels.Contains(_file.Revisions[i].Label) && !_usedLabels.Contains(_file.Revisions[i].Label))
                {
                    Warn("unused-revision", "revisions[" + i + "]", "No task runs this revision.");
                }
            }
        }

        // Every placeholder in a text must be known, take the right argument, and name a declared binding.
        private void CheckText(string text, string path)
        {
            foreach (var token in Placeholders.Scan(text))
            {
                if (token.Name == Placeholders.BindName)
                {
                    if (string.IsNullOrWhiteSpace(token.Argument))
                    {
                        Error("bad-placeholder", path, "{bind:...} needs the name of a binding.");
                    }
                    else if (!_declaredBindings.Contains(token.Argument))
                    {
                        Error("unknown-binding", path, "{bind:" + token.Argument + "} names a binding that is not declared.");
                    }
                    else
                    {
                        _usedBindings.Add(token.Argument);
                    }
                }
                else if (Placeholders.Names.Contains(token.Name))
                {
                    if (token.Argument is not null)
                    {
                        Error("bad-placeholder", path, "{" + token.Name + "} takes no argument.");
                    }
                }
                else
                {
                    Error("unknown-placeholder", path, "Unknown placeholder {" + token.Name + "}.");
                }
            }
        }

        // A name that ends up in a file or folder name: not empty, no path characters, unique.
        private void CheckName(string value, string path, HashSet<string> seen, string what)
        {
            if (IsBlank(value))
            {
                Error("empty-value", path, "The " + what + " must not be empty.");
            }
            else if (!IsFileNameSafe(value))
            {
                Error("bad-name", path, "The " + what + " must be at most " + MaxNameLength + " characters, without any of "
                    + UnsafeNameCharacters + " or control characters, and not start or end with a space or end with a dot.");
            }
            else if (!seen.Add(value))
            {
                Error("duplicate-name", path, "The " + what + " \"" + value + "\" is used twice.");
            }
        }

        private static bool IsFileNameSafe(string name) =>
            name.Length <= MaxNameLength
            && name == name.Trim()
            && !name.EndsWith('.')
            && name.All(c => c >= ' ' && !UnsafeNameCharacters.Contains(c));

        private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

        private void Error(string code, string path, string message) =>
            _issues.Add(new ValidationIssue(Severity.Error, code, path, message));

        private void Warn(string code, string path, string message) =>
            _issues.Add(new ValidationIssue(Severity.Warning, code, path, message));
    }
}
