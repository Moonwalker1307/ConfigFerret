using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ConfigFerret.Desktop.Credentials;

/// <summary>
/// The macOS login keychain. Each secret is a generic password with service <c>ConfigFerret</c> and the connection as
/// its account, and shows in Keychain Access. macOS may ask to allow access after the app is updated, since the new
/// build is a different binary.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainStore : ICredentialStore
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;

    /// <inheritdoc />
    public string DisplayName => "macOS Keychain";

    /// <inheritdoc />
    public string? Read(string account)
    {
        var service = Encoding.UTF8.GetBytes(CredentialStores.ServiceName);
        var name = Encoding.UTF8.GetBytes(account);
        var status = NativeMethods.SecKeychainFindGenericPassword(
            IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, out var length, out var data, out var item);
        if (status == ErrSecItemNotFound)
        {
            return null;
        }

        Check(status, "read");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
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
            _ = NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, data);
            Release(item);
        }
    }

    /// <inheritdoc />
    public void Write(string account, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var service = Encoding.UTF8.GetBytes(CredentialStores.ServiceName);
        var name = Encoding.UTF8.GetBytes(account);
        var password = Encoding.UTF8.GetBytes(secret);
        try
        {
            var status = NativeMethods.FindItem(
                IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, IntPtr.Zero, IntPtr.Zero, out var item);
            if (status == ErrSecSuccess)
            {
                try
                {
                    Check(NativeMethods.SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password), "save");
                }
                finally
                {
                    Release(item);
                }

                return;
            }

            if (status != ErrSecItemNotFound)
            {
                Check(status, "save");
            }

            Check(
                NativeMethods.SecKeychainAddGenericPassword(
                    IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, (uint)password.Length, password, IntPtr.Zero),
                "save");
        }
        finally
        {
            Array.Clear(password);
        }
    }

    /// <inheritdoc />
    public void Delete(string account)
    {
        var service = Encoding.UTF8.GetBytes(CredentialStores.ServiceName);
        var name = Encoding.UTF8.GetBytes(account);
        var status = NativeMethods.FindItem(
            IntPtr.Zero, (uint)service.Length, service, (uint)name.Length, name, IntPtr.Zero, IntPtr.Zero, out var item);
        if (status == ErrSecItemNotFound)
        {
            return;
        }

        Check(status, "remove");
        try
        {
            Check(NativeMethods.SecKeychainItemDelete(item), "remove");
        }
        finally
        {
            Release(item);
        }
    }

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
        {
            throw new CredentialStoreException($"Could not {action} the credential in the macOS Keychain (OSStatus {status}).");
        }
    }

    private static void Release(IntPtr item)
    {
        if (item != IntPtr.Zero)
        {
            NativeMethods.CFRelease(item);
        }
    }

    private static class NativeMethods
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(Security)]
        internal static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemRef);

        // The same call without reading the password, to find the item only.
        [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
        internal static extern int FindItem(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            IntPtr passwordLength,
            IntPtr passwordData,
            out IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

        [DllImport(Security)]
        internal static extern int SecKeychainItemDelete(IntPtr itemRef);

        [DllImport(Security)]
        internal static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr cf);
    }
}
