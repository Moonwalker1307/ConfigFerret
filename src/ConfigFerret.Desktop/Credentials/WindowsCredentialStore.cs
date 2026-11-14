using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ConfigFerret.Desktop.Credentials;

/// <summary>
/// Windows Credential Manager (generic credentials of the signed-in user, encrypted with DPAPI by Windows). Each secret
/// is saved as <c>ConfigFerret:{account}</c> and shows in Control Panel, Credential Manager, Windows Credentials.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    /// <inheritdoc />
    public string DisplayName => "Windows Credential Manager";

    /// <inheritdoc />
    public string? Read(string account)
    {
        if (!NativeMethods.CredRead(Target(account), CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw Failure("read", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    /// <inheritdoc />
    public void Write(string account, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                Comment = "ConfigFerret desktop connection credential",
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = account,
            };
            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw Failure("save", Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // Wipe the copies of the secret this method made.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }
    }

    /// <inheritdoc />
    public void Delete(string account)
    {
        if (!NativeMethods.CredDelete(Target(account), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw Failure("remove", error);
            }
        }
    }

    private static string Target(string account) => CredentialStores.ServiceName + ":" + account;

    private static CredentialStoreException Failure(string action, int error) =>
        new($"Could not {action} the credential in Windows Credential Manager: {new Win32Exception(error).Message}");

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void CredFree(IntPtr buffer);
    }
}
