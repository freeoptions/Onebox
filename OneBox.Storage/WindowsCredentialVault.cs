using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OneBox.Core;

namespace OneBox.Storage;

public sealed class WindowsCredentialVault : ICredentialVault
{
    private const int GenericCredential = 1;
    private const int LocalMachinePersistence = 2;
    private static string Target(Guid accountId) => $"OneBox/account/{accountId:N}";
    private static string GoogleClientTarget(string clientId) =>
        "OneBox/google-client/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)));

    public void SaveGoogleClientSecret(string clientId, string secret) => SaveTarget(GoogleClientTarget(clientId), secret);

    public string? ReadGoogleClientSecret(string clientId) => ReadTarget(GoogleClientTarget(clientId));

    public void Save(Guid accountId, string secret) => SaveTarget(Target(accountId), secret);

    private static void SaveTarget(string target, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560) throw new ArgumentOutOfRangeException(nameof(secret));
        var buffer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = buffer,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) bytes[index] = 0;
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    public string? Read(Guid accountId) => ReadTarget(Target(accountId));

    private static string? ReadTarget(string target)
    {
        if (!CredRead(target, GenericCredential, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public void Delete(Guid accountId)
    {
        if (CredDelete(Target(accountId), GenericCredential, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);
}
