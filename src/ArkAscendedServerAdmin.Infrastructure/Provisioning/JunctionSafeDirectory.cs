namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// Deletes a directory tree without ever following a reparse point (plan step 27): junctions and symbolic
/// links inside the tree are removed as entries and their targets are left alone. This replaces
/// <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c> wherever an instance tree may be
/// involved, because that overload calls <c>DeleteVolumeMountPoint</c> on every junction it meets, which
/// fails with access denied when the process is not elevated even though the junction itself is removable.
/// </summary>
public static class JunctionSafeDirectory
{
    /// <summary>Removes <paramref name="path"/> and everything beneath it; a no-op when it does not exist.</summary>
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = new DirectoryInfo(path);
        if (directory.Exists)
        {
            Delete(directory);
        }
    }

    /// <summary>Removes the entry: a reparse point as a single entry, a real directory after its contents, a file directly.</summary>
    public static void Delete(FileSystemInfo entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry is not DirectoryInfo directory)
        {
            entry.Delete();
            return;
        }

        if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            foreach (var child in directory.EnumerateFileSystemInfos())
            {
                Delete(child);
            }
        }

        directory.Delete(recursive: false);
    }
}
