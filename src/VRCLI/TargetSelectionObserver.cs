using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed class TargetSelectionObserver(
    string requestFile,
    string responseFile,
    TerminalProgressRenderer terminal,
    IProcessLineObserver? next = null) : IProcessLineObserver
{
    private int prompted;

    public async ValueTask<bool> OnLineAsync(string line, bool isError, CancellationToken cancellationToken)
    {
        bool handled = next != null && await next.OnLineAsync(line, isError, cancellationToken);
        if (!ProgressLine.TryParse(line, out ProgressLine? progress) ||
            progress!.Area != "TARGET" ||
            !progress.StartsPhase ||
            !progress.Message.StartsWith("Multiple avatars found", StringComparison.Ordinal) ||
            Interlocked.Exchange(ref prompted, 1) != 0)
            return handled;

        TargetSelectionRequest request = await ReadRequestAsync(cancellationToken);
        string? selector = terminal.PromptForTarget(request.Targets);
        string temporary = responseFile + ".tmp";
        await File.WriteAllTextAsync(temporary, selector ?? TargetSelectionRequest.Cancelled, cancellationToken);
        File.Move(temporary, responseFile, true);
        return true;
    }

    private async Task<TargetSelectionRequest> ReadRequestAsync(CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(requestFile) && DateTime.UtcNow < deadline)
            await Task.Delay(50, cancellationToken);
        if (!File.Exists(requestFile)) throw new InvalidOperationException("Unity requested avatar selection without a candidate list.");

        await using FileStream stream = File.OpenRead(requestFile);
        return await JsonSerializer.DeserializeAsync<TargetSelectionRequest>(stream, cancellationToken: cancellationToken) ??
               throw new InvalidOperationException("Unity returned an invalid avatar candidate list.");
    }
}

public sealed record TargetSelectionRequest(IReadOnlyList<ContentTarget> Targets)
{
    public const string Cancelled = "__VRCLI_CANCELLED__";
}
