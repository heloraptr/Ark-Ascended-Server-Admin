using System.Text;
using ArkAscendedServerAdmin.Domain;

namespace ArkAscendedServerAdmin.Infrastructure.Provisioning;

/// <summary>
/// Temp + rename writes shared by the INI source store and the generated-config writer (plan step 16).
/// The temp file lives in the target's directory so the final <see cref="File.Move(string, string, bool)"/>
/// is a same-volume rename, which NTFS performs atomically; a reader never sees a half-written file.
/// Text is UTF-8 without a byte-order mark.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static UTF8Encoding Utf8NoBom => _utf8NoBom;

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temp = await WriteTempAsync(path, text, cancellationToken);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Like <see cref="WriteAllTextAsync"/>, but the previous file (when there is one) becomes
    /// <paramref name="backupPath"/>, replacing any earlier backup: exactly one <c>.bak</c> is kept.
    /// </summary>
    public static async Task WriteAllTextWithBackupAsync(string path, string text, string backupPath, CancellationToken cancellationToken)
    {
        var temp = await WriteTempAsync(path, text, cancellationToken);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task<string> WriteTempAsync(string path, string text, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new ArgumentException("A rooted file path is required.", nameof(path));
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, text, _utf8NoBom, cancellationToken);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return temp;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort; the original failure is what the caller sees.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}

/// <summary>The on-disk names of the two ASA configuration files.</summary>
internal static class IniFileNames
{
    public const string Game = "Game.ini";
    public const string GameUserSettings = "GameUserSettings.ini";

    public static string For(IniFile file) => file == IniFile.Game ? Game : GameUserSettings;
}
