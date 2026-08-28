using System.ComponentModel;
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

public sealed class VrchatSessionStore
{
    private const string TargetPrefix = "VRCLI:VRChatSession:";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;

    public IReadOnlyList<SavedVrchatSession> List()
    {
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
        if (!OperatingSystem.IsWindows()) return;
        if (!CredDelete(TargetPrefix + userId, GenericCredential, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error);
        }
    }

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
