using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed class AuthApplication(
    TextWriter output,
    TextWriter error,
    IVrchatSessionStore? sessionStore = null)
{
    private readonly IVrchatSessionStore store = sessionStore ?? new VrchatSessionStore();

    public static bool ShouldHandle(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "auth", StringComparison.OrdinalIgnoreCase);

    public async Task<int> RunAsync(string[] args)
    {
        bool json = args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
        bool all = args.Any(argument => string.Equals(argument, "--all", StringComparison.OrdinalIgnoreCase));
        string[] knownOptions = ["--json", "--plain", "--all"];
        string? unknown = args.Skip(1).FirstOrDefault(argument =>
            argument.StartsWith("--", StringComparison.Ordinal) &&
            !knownOptions.Contains(argument, StringComparer.OrdinalIgnoreCase));
        if (unknown != null) return await FailAsync("Unknown auth option: " + unknown, json);

        string[] positional = args.Skip(1)
            .Where(argument => !knownOptions.Contains(argument, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (positional.Length == 0)
            return await FailAsync("Use 'vrcli auth list' or 'vrcli auth logout <account>'.", json);

        IReadOnlyList<SavedVrchatSession> sessions = store.List();
        if (string.Equals(positional[0], "list", StringComparison.OrdinalIgnoreCase) && positional.Length == 1)
        {
            object[] result = sessions.Select(session => new
            {
                session.UserId,
                session.DisplayName,
                session.LoginHint,
                session.LastUsed
            }).Cast<object>().ToArray();
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    new { Success = true, Sessions = result },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (result.Length == 0)
            {
                await output.WriteLineAsync("No saved VRChat sessions.");
            }
            else
            {
                foreach (SavedVrchatSession session in sessions)
                    await output.WriteLineAsync($"{session.DisplayName}  {session.UserId}  {session.LoginHint}  {session.LastUsed:O}");
            }
            return ExitCodes.Success;
        }

        if (!string.Equals(positional[0], "logout", StringComparison.OrdinalIgnoreCase))
            return await FailAsync("Unknown auth command: " + positional[0], json);

        if (all && positional.Length != 1)
            return await FailAsync("Use either 'auth logout <account>' or 'auth logout --all'.", json);
        IReadOnlyList<SavedVrchatSession> selected;
        if (all)
        {
            selected = sessions;
        }
        else
        {
            if (positional.Length != 2)
                return await FailAsync("auth logout requires an account display name, login hint, or user ID.", json);
            selected = VrchatSessionStore.Match(sessions, positional[1]);
            if (selected.Count == 0)
                return await FailAsync("No saved session matches " + positional[1] + ".", json);
            if (selected.Count > 1)
                return await FailAsync("Several sessions match; use the exact user ID.", json);
        }

        foreach (SavedVrchatSession session in selected) store.Delete(session.UserId);
        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new { Success = true, Removed = selected.Select(session => session.UserId) },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            await output.WriteLineAsync(selected.Count == 0
                ? "No saved VRChat sessions."
                : $"Removed {selected.Count} saved VRChat session(s).");
        }
        return ExitCodes.Success;
    }

    private async Task<int> FailAsync(string message, bool json)
    {
        if (json)
            await output.WriteLineAsync(JsonSerializer.Serialize(new
            {
                Success = false,
                ExitCode = ExitCodes.InvalidArguments,
                Message = message
            }));
        else
            await error.WriteLineAsync("VRCLI: " + message);
        return ExitCodes.InvalidArguments;
    }
}
