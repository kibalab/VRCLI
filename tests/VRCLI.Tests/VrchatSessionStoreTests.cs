using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class VrchatSessionStoreTests
{
    [Fact]
    public void SavesListsAndDeletesWindowsCredential()
    {
        if (!OperatingSystem.IsWindows()) return;
        string userId = "usr_test_" + Guid.NewGuid().ToString("N");
        VrchatSessionStore store = new();
        SavedVrchatSession expected = new(
            userId,
            "Test Account",
            "test@example.com",
            new VrchatSessionTokens("auth-token", "two-factor-token"),
            DateTimeOffset.UtcNow);

        try
        {
            store.Save(expected);

            SavedVrchatSession actual = Assert.Single(store.List(), session => session.UserId == userId);
            Assert.Equal(expected.DisplayName, actual.DisplayName);
            Assert.Equal(expected.LoginHint, actual.LoginHint);
            Assert.Equal(expected.Tokens, actual.Tokens);
        }
        finally
        {
            store.Delete(userId);
        }
    }
}
