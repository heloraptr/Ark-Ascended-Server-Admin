using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// NTFS junction (mount-point reparse point) primitives over <c>DeviceIoControl</c> (plan step 27). No
/// shell-out to <c>mklink</c> and no <c>SeCreateSymbolicLinkPrivilege</c>; the reparse data written is the
/// same shape <c>mklink /J</c> produces, so junctions made either way are interchangeable on disk.
/// </summary>
internal static class NtfsJunction
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorNotAReparsePoint = 4390;
    private const int MaximumReparseDataBufferSize = 16 * 1024;

    /// <summary><c>ReparseTag</c> (4) + <c>ReparseDataLength</c> (2) + <c>Reserved</c> (2).</summary>
    private const int ReparseHeaderSize = 8;

    /// <summary>The four <c>USHORT</c> offsets/lengths of a mount-point buffer that follow the header.</summary>
    private const int MountPointHeaderSize = 8;

    private const int PathBufferOffset = ReparseHeaderSize + MountPointHeaderSize;
    private const string NtPathPrefix = @"\??\";

    /// <summary>Creates the link directory and turns it into a junction to <paramref name="target"/> (a full path).</summary>
    public static void Create(string link, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(NtPathPrefix + target);
        var print = Encoding.Unicode.GetBytes(target);
        var pathBufferSize = substitute.Length + 2 + print.Length + 2;
        var reparseDataLength = MountPointHeaderSize + pathBufferSize;
        var buffer = new byte[ReparseHeaderSize + reparseDataLength];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)reparseDataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], checked((ushort)print.Length));
        substitute.CopyTo(span[PathBufferOffset..]);
        print.CopyTo(span[(PathBufferOffset + substitute.Length + 2)..]);

        Directory.CreateDirectory(link);
        try
        {
            using var handle = Open(link, GenericWrite);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length, null, 0, out _, IntPtr.Zero))
            {
                throw new IOException($"Could not create the junction {link} -> {target}.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        catch
        {
            TryRemoveEmptyDirectory(link);
            throw;
        }
    }

    /// <summary>
    /// The junction's target (the NT substitute name without its <c>\??\</c> prefix), or null when the
    /// path is a plain file or directory, a symbolic link, or any other kind of reparse point.
    /// </summary>
    public static string? TryReadTarget(string path)
    {
        using var handle = Open(path, 0);
        var buffer = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, null, 0, buffer, (uint)buffer.Length, out var bytesReturned, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorNotAReparsePoint
                ? null
                : throw new IOException($"Could not read the reparse point at {path}.", new Win32Exception(error));
        }

        var span = buffer.AsSpan(0, (int)bytesReturned);
        if (span.Length < PathBufferOffset || BinaryPrimitives.ReadUInt32LittleEndian(span) != IoReparseTagMountPoint)
        {
            return null;
        }

        var substituteOffset = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
        var substituteLength = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
        var substitute = Encoding.Unicode.GetString(span.Slice(PathBufferOffset + substituteOffset, substituteLength));
        return substitute.StartsWith(NtPathPrefix, StringComparison.Ordinal) ? substitute[NtPathPrefix.Length..] : substitute;
    }

    private static SafeFileHandle Open(string path, uint access)
    {
        var handle = CreateFile(path, access, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException($"Could not open {path}.", new Win32Exception(error));
    }

    private static void TryRemoveEmptyDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
            // Best effort: the caller is already propagating the real failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        [In] byte[]? lpInBuffer,
        uint nInBufferSize,
        [Out] byte[]? lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
