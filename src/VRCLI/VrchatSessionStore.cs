using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed record SavedVrchatSession(
    string UserId,
    string DisplayName,
    string LoginHint,
    VrchatSessionTokens Tokens,
    DateTimeOffset LastUsed);

public interface IVrchatSessionStore
{
    IReadOnlyList<SavedVrchatSession> List();
    void Save(SavedVrchatSession session);
    void Delete(string userId);
}

public sealed class VrchatSessionStore : IVrchatSessionStore
{
    private const string TargetPrefix = "VRCLI:VRChatSession:";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const string MacService = "VRCLI VRChat Sessions";

    public static string StorageDescription => OperatingSystem.IsWindows()
        ? "Windows Credential Manager"
        : OperatingSystem.IsMacOS() ? "macOS Keychain" : "process memory";

    public IReadOnlyList<SavedVrchatSession> List()
    {
        if (OperatingSystem.IsMacOS()) return LoadMacSessions();
        if (!OperatingSystem.IsWindows()) return [];
        if (!CredEnumerate(TargetPrefix + "*", 0, out uint count, out IntPtr credentials))
        {
            int error = Marshal.GetLastWin32Error();
            return error == 1168 ? [] : throw new Win32Exception(error);
        }

        try
        {
            List<SavedVrchatSession> sessions = [];
            for (int index = 0; index < count; index++)
            {
                IntPtr credentialPointer = Marshal.ReadIntPtr(credentials, index * IntPtr.Size);
                NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
                SavedVrchatSession? session = Deserialize(credential);
                if (session != null) sessions.Add(session);
            }
            return sessions.OrderByDescending(session => session.LastUsed).ToArray();
        }
        finally
        {
            CredFree(credentials);
        }
    }

    public void Save(SavedVrchatSession session)
    {
        if (OperatingSystem.IsMacOS())
        {
            List<SavedVrchatSession> sessions = LoadMacSessions()
                .Where(value => value.UserId != session.UserId)
                .Append(session)
                .OrderByDescending(value => value.LastUsed)
                .ToList();
            WriteMacSessions(sessions);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        byte[] secret = JsonSerializer.SerializeToUtf8Bytes(session);
        IntPtr blob = Marshal.AllocCoTaskMem(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            NativeCredential credential = new()
            {
                Type = GenericCredential,
                TargetName = TargetPrefix + session.UserId,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = session.DisplayName,
                Comment = session.LoginHint
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string userId)
    {
        if (OperatingSystem.IsMacOS())
        {
            WriteMacSessions(LoadMacSessions().Where(session => session.UserId != userId).ToArray());
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        if (!CredDelete(TargetPrefix + userId, GenericCredential, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
        }
    }

    public static IReadOnlyList<SavedVrchatSession> Match(
        IEnumerable<SavedVrchatSession> sessions,
        string login) => sessions.Where(session =>
            string.Equals(session.UserId, login, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.DisplayName, login, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.LoginHint, login, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(session => session.LastUsed)
        .ToArray();

    internal static string SerializeMacPayload(IReadOnlyList<SavedVrchatSession> sessions) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(sessions));

    internal static IReadOnlyList<SavedVrchatSession> DeserializeMacPayload(string payload)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(payload.Trim());
            try
            {
                return JsonSerializer.Deserialize<SavedVrchatSession[]>(bytes) ?? [];
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new InvalidDataException("The VRCLI macOS Keychain session entry is invalid.", exception);
        }
    }

    private static IReadOnlyList<SavedVrchatSession> LoadMacSessions()
    {
        SecurityResult result = RunSecurity(
            "find-generic-password", "-s", MacService, "-a", Environment.UserName, "-w");
        if (result.ExitCode == 44) return [];
        if (result.ExitCode != 0)
            throw new InvalidOperationException("macOS Keychain could not read VRCLI sessions: " + result.Error.Trim());
        return DeserializeMacPayload(result.Output);
    }

    private static void WriteMacSessions(IReadOnlyList<SavedVrchatSession> sessions)
    {
        if (sessions.Count == 0)
        {
            SecurityResult deleted = RunSecurity(
                "delete-generic-password", "-s", MacService, "-a", Environment.UserName);
            if (deleted.ExitCode is not (0 or 44))
                throw new InvalidOperationException("macOS Keychain could not remove VRCLI sessions: " + deleted.Error.Trim());
            return;
        }

        string payload = SerializeMacPayload(sessions);
        try
        {
            SecurityResult result = RunSecurity(
                "add-generic-password", "-U", "-s", MacService, "-a", Environment.UserName, "-w", payload);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("macOS Keychain could not save VRCLI sessions: " + result.Error.Trim());
        }
        finally
        {
            payload = string.Empty;
        }
    }

    private static SecurityResult RunSecurity(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("/usr/bin/security")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("macOS Keychain command could not be started.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new SecurityResult(process.ExitCode, output, error);
    }

    private sealed record SecurityResult(int ExitCode, string Output, string Error);

    private static SavedVrchatSession? Deserialize(NativeCredential credential)
    {
        if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
        byte[] secret = new byte[credential.CredentialBlobSize];
        try
        {
            Marshal.Copy(credential.CredentialBlob, secret, 0, secret.Length);
            return JsonSerializer.Deserialize<SavedVrchatSession>(secret);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            Array.Clear(secret, 0, secret.Length);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerate(
        string filter,
        uint flags,
        out uint count,
        out IntPtr credentials);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
