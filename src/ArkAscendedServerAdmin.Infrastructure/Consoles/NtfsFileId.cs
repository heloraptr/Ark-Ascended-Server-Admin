using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArkAscendedServerAdmin.Infrastructure.Consoles;

/// <summary>
/// The volume serial plus 64-bit file index that identify an open file on NTFS. Two handles to the same
/// path yield different ids after the game rotates <c>ShooterGame.log</c> (rename + create), even though
/// the new file inherits the old creation time (NTFS tunneling, Phase 2 spike).
/// </summary>
internal readonly record struct NtfsFileId(uint VolumeSerialNumber, ulong FileIndex)
{
    /// <summary>Reads the id through <c>GetFileInformationByHandle</c>; throws <see cref="IOException"/> on failure.</summary>
    public static NtfsFileId FromHandle(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!NativeMethods.GetFileInformationByHandle(handle, out var info))
        {
            throw new IOException("GetFileInformationByHandle failed.", Marshal.GetHRForLastWin32Error());
        }

        return new NtfsFileId(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
    }
}
