using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace KibaLab.VRCLI;

public sealed class InteractiveTwoFactorServer : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly TerminalProgressRenderer renderer;
    private readonly ConcurrentBag<string> secrets;
    private readonly Func<IReadOnlyList<string>, InteractiveTwoFactorAnswer>? prompt;

    public InteractiveTwoFactorServer(
        TerminalProgressRenderer renderer,
        ConcurrentBag<string> secrets,
        Func<IReadOnlyList<string>, InteractiveTwoFactorAnswer>? prompt = null)
    {
        this.renderer = renderer;
        this.secrets = secrets;
        this.prompt = prompt;
        PipeName = "vrcli-2fa-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public string PipeName { get; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            using StreamReader reader = new(pipe, leaveOpen: true);
            using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
            string? requestJson = await reader.ReadLineAsync(cancellationToken);
            TwoFactorPromptRequest? request = requestJson == null
                ? null
                : JsonSerializer.Deserialize<TwoFactorPromptRequest>(requestJson);
            if (request?.Methods == null || request.Methods.Length == 0)
            {
                await writer.WriteLineAsync("{}");
                return;
            }

            InteractiveTwoFactorAnswer answer = prompt == null
                ? renderer.PromptForTwoFactor(request.Methods)
                : renderer.PromptForTwoFactor(() => prompt(request.Methods));
            secrets.Add(answer.Code);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new TwoFactorPromptResponse(
                answer.Method,
                answer.Code)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        pipe.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record TwoFactorPromptRequest(string[] Methods);
    private sealed record TwoFactorPromptResponse(string Method, string Code);
}
