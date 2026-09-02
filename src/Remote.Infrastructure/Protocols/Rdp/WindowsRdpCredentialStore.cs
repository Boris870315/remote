using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Remote.Infrastructure.Protocols.Rdp;

/// <summary>Stores RDP credentials in the current Windows user's Credential Manager.</summary>
public sealed class WindowsRdpCredentialStore : IRdpCredentialStore
{
    public Task StoreAsync(
        Uri endpoint,
        string username,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is available on Windows only.");
        }

        var password = Encoding.UTF8.GetString(passwordUtf8.Span);
        var blob = Encoding.Unicode.GetBytes(password);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.DomainPassword,
                TargetName = $"TERMSRV/{endpoint.Host}",
                CredentialBlobSize = checked((uint)blob.Length),
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredentialPersistence.LocalMachine,
                UserName = username,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to store the RDP credential.");
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(blob);
            handle.Free();
        }

        return Task.CompletedTask;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    private enum CredentialType : uint
    {
        DomainPassword = 2,
    }

    private enum CredentialPersistence : uint
    {
        LocalMachine = 2,
    }
}
