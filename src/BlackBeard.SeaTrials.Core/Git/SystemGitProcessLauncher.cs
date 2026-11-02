using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BlackBeard.SeaTrials.Core.Git;

/// <summary>Starts git as a child process, with no shell, and reads its output. This is the only code that starts git.</summary>
public sealed class SystemGitProcessLauncher : IGitProcessLauncher
{
    public async Task<GitResult> LaunchAsync(GitLaunch launch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!Directory.Exists(launch.WorkingDirectory))
        {
            throw new GitException(GitFailureKind.InvalidArgument, "The folder " + launch.WorkingDirectory + " does not exist.");
        }

        var info = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = launch.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in launch.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in launch.RemovedEnvironment)
        {
            info.Environment.Remove(name);
        }

        foreach (var pair in launch.Environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            throw new GitException(
                GitFailureKind.GitNotFound,
                "git could not be started (" + launch.Executable + "). Is it installed and on the path?",
                null,
                error);
        }

        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(launch.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var output = process.StandardOutput.ReadToEndAsync(linked.Token);
        var errorOutput = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            var errors = await errorOutput.ConfigureAwait(false);
            return new GitResult(process.ExitCode, text, errors);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            throw new GitException(GitFailureKind.Timeout, "git did not finish within " + launch.Timeout + ".");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
