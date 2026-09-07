using ArkAscendedServerAdmin.Configuration;
using ArkAscendedServerAdmin.Provisioning;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// Builds and tears down the per-instance junction tree (plan step 27, DESIGN §4) and retires
/// <c>ShooterGame\Saved</c> on delete (plan step 30). Exactly what Spike B proved sufficient is linked:
/// <c>Engine</c>, <c>ShooterGame\Binaries</c>, <c>ShooterGame\Content</c>, <c>ShooterGame\Plugins</c>;
/// <c>Saved</c> is real. Junctions are created through <see cref="NtfsJunction"/>, never
/// <see cref="Directory.CreateSymbolicLink"/>. Removal only ever deletes a junction entry itself, so the
/// shared install under <c>Server\</c> cannot be touched by an instance operation.
/// </summary>
public sealed class InstanceLayoutService(DataRootLayout layout, TimeProvider timeProvider) : IInstanceLayoutService
{
    private const string ShooterGameDirectoryName = "ShooterGame";

    /// <summary>Relative to both the instance directory (link) and <c>Server\</c> (target).</summary>
    private static readonly string[] _junctionPaths =
    [
        "Engine",
        Path.Combine(ShooterGameDirectoryName, "Binaries"),
        Path.Combine(ShooterGameDirectoryName, "Content"),
        Path.Combine(ShooterGameDirectoryName, "Plugins"),
    ];

    public Task EnsureAsync(string slug, CancellationToken cancellationToken)
    {
        ValidateSlug(slug);
        return Task.Run(() => Ensure(slug), cancellationToken);
    }

    public bool IsComplete(string slug)
    {
        ValidateSlug(slug);

        var saved = layout.InstanceSavedDirectory(slug);
        if (!Directory.Exists(saved) || IsReparsePoint(saved))
        {
            return false;
        }

        foreach (var (link, target) in Junctions(slug))
        {
            if (!Directory.Exists(link))
            {
                return false;
            }

            var current = NtfsJunction.TryReadTarget(link);
            if (current is null || !PathsEqual(current, target))
            {
                return false;
            }
        }

        return true;
    }

    public Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken)
    {
        ValidateSlug(slug);
        return Task.Run(() => RemoveAllExceptSaved(slug), cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>Returns null with <paramref name="keepWorldData"/> when there was no <c>Saved</c> directory to archive.</remarks>
    public Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken)
    {
        ValidateSlug(slug);
        return Task.Run(() => Retire(slug, keepWorldData), cancellationToken);
    }

    private void Ensure(string slug)
    {
        // Creates the instance directory, ShooterGame, the real Saved, and Saved\Config\WindowsServer in one go.
        Directory.CreateDirectory(layout.InstanceGeneratedConfigDirectory(slug));

        foreach (var (link, target) in Junctions(slug))
        {
            EnsureJunction(link, target);
        }
    }

    private static void EnsureJunction(string link, string target)
    {
        if (Directory.Exists(link) || File.Exists(link))
        {
            var current = NtfsJunction.TryReadTarget(link)
                ?? throw new IOException($"{link} exists and is not a junction; move it aside and retry.");

            if (PathsEqual(current, target))
            {
                return;
            }

            Directory.Delete(link, recursive: false);
        }

        NtfsJunction.Create(link, target);
    }

    private string? Retire(string slug, bool keepWorldData)
    {
        var saved = layout.InstanceSavedDirectory(slug);
        string? archivePath = null;

        if (Directory.Exists(saved))
        {
            if (keepWorldData)
            {
                Directory.CreateDirectory(layout.Archive);
                archivePath = UniqueArchivePath(slug);
                Directory.Move(saved, archivePath);
            }
            else
            {
                JunctionSafeDirectory.Delete(saved);
            }
        }

        // Saved is gone either way, so this removes every remaining entry and the instance directory itself.
        RemoveAllExceptSaved(slug);
        return archivePath;
    }

    /// <summary>
    /// Deletes the junctions (as entries, never their targets) and everything else under the instance
    /// directory except the <c>ShooterGame\Saved</c> subtree; when <c>Saved</c> does not exist the
    /// instance directory is removed entirely.
    /// </summary>
    private void RemoveAllExceptSaved(string slug)
    {
        var instanceDirectory = layout.InstanceDirectory(slug);
        if (!Directory.Exists(instanceDirectory))
        {
            return;
        }

        var shooterGame = Path.Combine(instanceDirectory, ShooterGameDirectoryName);
        var saved = layout.InstanceSavedDirectory(slug);
        var keepSaved = Directory.Exists(saved) && !IsReparsePoint(shooterGame);

        foreach (var entry in new DirectoryInfo(instanceDirectory).EnumerateFileSystemInfos())
        {
            if (keepSaved && PathsEqual(entry.FullName, shooterGame))
            {
                foreach (var inner in new DirectoryInfo(shooterGame).EnumerateFileSystemInfos())
                {
                    if (!PathsEqual(inner.FullName, saved))
                    {
                        JunctionSafeDirectory.Delete(inner);
                    }
                }

                continue;
            }

            JunctionSafeDirectory.Delete(entry);
        }

        if (!keepSaved)
        {
            Directory.Delete(instanceDirectory, recursive: false);
        }
    }

    private string UniqueArchivePath(string slug)
    {
        var path = layout.ArchiveDirectory(slug, timeProvider.GetUtcNow());
        var candidate = path;
        for (var i = 2; Directory.Exists(candidate); i++)
        {
            candidate = $"{path}-{i}";
        }

        return candidate;
    }

    private IEnumerable<(string Link, string Target)> Junctions(string slug)
    {
        var instanceDirectory = layout.InstanceDirectory(slug);
        foreach (var relative in _junctionPaths)
        {
            yield return (Path.Combine(instanceDirectory, relative), Path.Combine(layout.Server, relative));
        }
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void ValidateSlug(string slug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        if (slug is "." or ".." || slug.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{slug}' is not a valid slug.", nameof(slug));
        }
    }
}
