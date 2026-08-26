using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using KibaLab.VRCLI;

namespace VRCLI.Tests;

public sealed class InteractiveTwoFactorServerTests
{
    [Fact]
    public async Task ExchangesSelectedMethodAndCodeOverPrivatePipe()
    {
        StringWriter output = new();
        TerminalProgressRenderer renderer = new(output);
        renderer.Start();
        ConcurrentBag<string> secrets = new();
        await using InteractiveTwoFactorServer server = new(
            renderer,
            secrets,
            methods =>
            {
                Assert.Contains("totp", methods);
                return new InteractiveTwoFactorAnswer("totp", "123456");
            });
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task serverTask = server.RunAsync(cancellation.Token);

        using NamedPipeClientStream client = new(
            ".",
            server.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        using StreamReader reader = new(client, Encoding.UTF8, leaveOpen: true);
        using StreamWriter writer = new(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("{\"Methods\":[\"totp\",\"otp\"]}");

        string? response = await reader.ReadLineAsync(cancellation.Token);
        await serverTask;

        using JsonDocument json = JsonDocument.Parse(response!);
        Assert.Equal("totp", json.RootElement.GetProperty("Method").GetString());
        Assert.Equal("123456", json.RootElement.GetProperty("Code").GetString());
        Assert.Contains("123456", secrets);
    }
}
