using System.Diagnostics;

namespace KibaLab.VRCLI;

public sealed record ChildProcessResult(int ExitCode, bool TimedOut);

public interface IProcessLineObserver
{
    ValueTask<bool> OnLineAsync(string line, bool isError, CancellationToken cancellationToken);
}

public static class ChildProcess
{
    public static async Task<ChildProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TextWriter output,
        TextWriter error,
        TimeSpan timeout,
        IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken,
        IProcessLineObserver? observer = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        Task stdout = PumpAsync(process.StandardOutput, output, false, secrets, observer, cancellationToken);
        Task stderr = PumpAsync(process.StandardError, error, true, secrets, observer, cancellationToken);
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr);
            return new ChildProcessResult(process.ExitCode, false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            TryKill(process);
            await Task.WhenAll(stdout, stderr);
            return new ChildProcessResult(ExitCodes.TimedOut, true);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static async Task PumpAsync(
        TextReader reader,
        TextWriter writer,
        bool isError,
        IReadOnlyCollection<string> secrets,
        IProcessLineObserver? observer,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            string redacted = line;
            foreach (string secret in secrets.Where(value => !string.IsNullOrEmpty(value)))
            {
                redacted = redacted.Replace(secret, "***", StringComparison.Ordinal);
            }
            bool handled = observer != null && await observer.OnLineAsync(redacted, isError, cancellationToken);
            if (!handled) await writer.WriteLineAsync(redacted);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
