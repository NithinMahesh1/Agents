using System.Diagnostics;

namespace Agents.Desktop;

/// <summary>
/// Testability seam over external process invocation. All CLI calls made by the drivers go
/// through this interface so tests can substitute a fake and assert on the exact argv without
/// ever spawning a real process.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="file"/> with the given argument vector (never a shell string) and
    /// returns its exit code, raw stdout bytes, and stderr text once it exits.
    /// </summary>
    /// <param name="file">Executable to run (resolved via PATH).</param>
    /// <param name="args">Arguments passed one-per-element; no shell interpolation occurs.</param>
    /// <param name="stdin">Optional bytes to write to the process's standard input.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        byte[]? stdin = null,
        CancellationToken ct = default);
}

/// <summary>Result of a completed process: its exit code, raw stdout bytes, and stderr text.</summary>
/// <param name="ExitCode">Process exit code (0 on success for the tools used here).</param>
/// <param name="StdOut">Raw standard-output bytes (kept binary so PNG/other data survives intact).</param>
/// <param name="StdErr">Decoded standard-error text, useful for diagnostics.</param>
public sealed record ProcessResult(int ExitCode, byte[] StdOut, string StdErr);

/// <summary>
/// Default <see cref="IProcessRunner"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Builds the command with <see cref="ProcessStartInfo.ArgumentList"/> (never a shell string),
/// so arguments containing spaces, quotes, or leading dashes are passed verbatim and cannot be
/// re-interpreted by a shell.
/// </summary>
public sealed class DefaultProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        byte[]? stdin = null,
        CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read stdout as raw bytes and stderr as text concurrently to avoid pipe-buffer deadlocks.
        await using var stdOutBuffer = new MemoryStream();
        var copyStdOut = process.StandardOutput.BaseStream.CopyToAsync(stdOutBuffer, ct);
        var readStdErr = process.StandardError.ReadToEndAsync(ct);

        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        await Task.WhenAll(copyStdOut, readStdErr).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdOutBuffer.ToArray(), readStdErr.Result);
    }
}
