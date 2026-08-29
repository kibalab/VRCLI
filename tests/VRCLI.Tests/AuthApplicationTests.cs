using System.Text.Json;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class AuthApplicationTests
{
    [Fact]
    public async Task ListsSessionMetadataWithoutTokens()
    {
        FakeSessionStore store = new(Session());
        StringWriter output = new();

        int exitCode = await new AuthApplication(output, new StringWriter(), store)
            .RunAsync(["auth", "list", "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.Equal("KIBA_", result.RootElement.GetProperty("Sessions")[0].GetProperty("DisplayName").GetString());
        Assert.DoesNotContain("secret-auth", output.ToString());
        Assert.DoesNotContain("secret-two-factor", output.ToString());
    }

    [Fact]
    public async Task LogsOutAnExactlyMatchedSession()
    {
        FakeSessionStore store = new(Session());

        int exitCode = await new AuthApplication(new StringWriter(), new StringWriter(), store)
            .RunAsync(["auth", "logout", "KIBA_"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(store.List());
    }

    private static SavedVrchatSession Session() => new(
        "usr_example",
        "KIBA_",
        "owner@example.com",
        new VrchatSessionTokens("secret-auth", "secret-two-factor"),
        DateTimeOffset.UtcNow);

    private sealed class FakeSessionStore(params SavedVrchatSession[] sessions) : IVrchatSessionStore
    {
        private readonly List<SavedVrchatSession> values = [.. sessions];

        public IReadOnlyList<SavedVrchatSession> List() => values.ToArray();
        public void Save(SavedVrchatSession session)
        {
            values.RemoveAll(value => value.UserId == session.UserId);
            values.Add(session);
        }
        public void Delete(string userId) => values.RemoveAll(value => value.UserId == userId);
    }
}
