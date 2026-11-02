using System.Diagnostics;
using System.Text;
using BlackBeard.SeaTrials.Core.Git;

namespace BlackBeard.SeaTrials.Core.Tests.Git;

/// <summary>A temporary folder that is deleted, read-only git files included, when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        // On Windows the temp path of a runner is an 8.3 short name, while git prints long names; build the long form.
        var baseDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
            : Path.GetTempPath();
        Root = Path.TrimEndingDirectorySeparator(Path.Combine(baseDirectory, "seatrials-" + Guid.NewGuid().ToString("N").Substring(0, 12)));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    public static void ForceDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => ForceDelete(Root);
}

/// <summary>Runs the real git for the tests, apart from the harness's own runner, to build and inspect repositories.</summary>
public static class RawGit
{
    private static long _clock = 1767225600; // 2026-01-01T00:00:00Z; every call moves it on, so commits have distinct times

    public static string NullDevice => OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

    public static string Ceiling => Path.TrimEndingDirectorySeparator(
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")
            : Path.GetTempPath());

    public static string Run(string directory, params string[] arguments) => RunWithInput(directory, null, arguments);

    public static string RunWithInput(string directory, string? input, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY" })
        {
            info.Environment.Remove(name);
        }

        var time = Interlocked.Increment(ref _clock).ToString(System.Globalization.CultureInfo.InvariantCulture) + " +0000";
        info.Environment["GIT_CONFIG_GLOBAL"] = NullDevice;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_CEILING_DIRECTORIES"] = Ceiling;
        info.Environment["GIT_AUTHOR_NAME"] = "Sample Author";
        info.Environment["GIT_AUTHOR_EMAIL"] = "noreply@example.invalid";
        info.Environment["GIT_COMMITTER_NAME"] = "Sample Author";
        info.Environment["GIT_COMMITTER_EMAIL"] = "noreply@example.invalid";
        info.Environment["GIT_AUTHOR_DATE"] = time;
        info.Environment["GIT_COMMITTER_DATE"] = time;

        using var process = Process.Start(info)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("git " + string.Join(" ", arguments) + " failed: " + error.Result);
        }

        return output.Result;
    }
}

/// <summary>A repository made with real git in a temporary folder. Commit ids are made at run time.</summary>
public sealed class TestRepo
{
    private TestRepo(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TestRepo Create(string folder)
    {
        Directory.CreateDirectory(folder);
        RawGit.Run(folder, "init", "-q", "-b", "main");
        return new TestRepo(folder);
    }

    public static TestRepo CreateBare(string folder)
    {
        Directory.CreateDirectory(folder);
        RawGit.Run(folder, "init", "-q", "--bare", "-b", "main");
        return new TestRepo(folder);
    }

    public static TestRepo Clone(string source, string folder)
    {
        var parent = System.IO.Path.GetDirectoryName(folder)!;
        Directory.CreateDirectory(parent);
        RawGit.Run(parent, "clone", "-q", source, folder);
        return new TestRepo(folder);
    }

    /// <summary>Writes the file, commits everything and returns the new commit's full id.</summary>
    public string Commit(string file, string content, string message)
    {
        var target = System.IO.Path.Combine(Path, file);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        Run("add", "-A");
        Run("commit", "-q", "-m", message);
        return Sha();
    }

    public string Sha(string rev = "HEAD") => Run("rev-parse", rev).Trim();

    /// <summary>Adds many commits with an empty tree to a branch in one go (git fast-import). Each is a minute after the last.</summary>
    public void ImportCommits(string branch, int count)
    {
        var stream = new StringBuilder();
        for (var i = 1; i <= count; i++)
        {
            var message = "commit " + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            stream.Append("commit refs/heads/").Append(branch).Append('\n');
            stream.Append("committer Sample Author <noreply@example.invalid> ").Append(1767300000 + (i * 60)).Append(" +0000\n");
            stream.Append("data ").Append(message.Length).Append('\n');
            stream.Append(message).Append('\n');
        }

        RawGit.RunWithInput(Path, stream.ToString(), "fast-import", "--quiet");
    }

    public string Run(params string[] arguments) => RawGit.Run(Path, arguments);
}

/// <summary>The harness's own git service, set up to ignore the machine's git configuration.</summary>
public static class TestGit
{
    public static GitProcessOptions Options() => new()
    {
        Environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = RawGit.NullDevice,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CEILING_DIRECTORIES"] = RawGit.Ceiling,
        },
        // A submodule in a temporary folder is fetched over the file protocol, which git refuses by default.
        Config = new[] { new KeyValuePair<string, string>("protocol.file.allow", "always") },
        Timeout = TimeSpan.FromMinutes(2),
    };

    public static GitRunner Runner(IGitProcessLauncher? launcher = null) => new(Options(), launcher);

    public static GitService Service(IGitProcessLauncher? launcher = null) => new(Runner(launcher));
}

/// <summary>The base of the tests that need a folder and a git service.</summary>
public abstract class GitTestBase : IDisposable
{
    protected GitTestBase()
    {
        Folder = new TempFolder();
        Service = TestGit.Service();
    }

    protected TempFolder Folder { get; }

    protected GitService Service { get; }

    protected TestRepo NewRepo(string name = "repo") => TestRepo.Create(Folder.Combine(name));

    /// <summary>A bare repository to act as a remote, and a clone of it that has one commit pushed.</summary>
    protected (TestRepo Remote, TestRepo Clone) NewRemoteAndClone(string name = "clone")
    {
        var remote = TestRepo.CreateBare(Folder.Combine("remote-of-" + name + ".git"));
        var clone = TestRepo.Create(Folder.Combine(name));
        clone.Run("remote", "add", "origin", remote.Path);
        clone.Commit("readme.txt", "first", "first commit");
        clone.Run("push", "-q", "-u", "origin", "main");
        return (remote, clone);
    }

    public void Dispose()
    {
        Folder.Dispose();
        GC.SuppressFinalize(this);
    }
}
