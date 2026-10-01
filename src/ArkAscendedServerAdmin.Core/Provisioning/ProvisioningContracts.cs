using ArkAscendedServerAdmin.Domain;
using ArkAscendedServerAdmin.Ini;

namespace ArkAscendedServerAdmin.Provisioning;

/// <summary>
/// The per-instance junction tree (plan step 27, DESIGN §4): <c>Engine</c>, <c>ShooterGame\Binaries</c>,
/// <c>ShooterGame\Content</c>, <c>ShooterGame\Plugins</c> as NTFS junctions into <c>Server\</c>, and a
/// real <c>ShooterGame\Saved</c>. Every method is idempotent.
/// </summary>
public interface IInstanceLayoutService
{
    /// <summary>Creates any missing directory or junction; repairs a junction whose target is wrong.</summary>
    Task EnsureAsync(string slug, CancellationToken cancellationToken);

    /// <summary>True when every junction exists and points into <c>Server\</c> and <c>Saved</c> is a real directory.</summary>
    bool IsComplete(string slug);

    /// <summary>Removes the junctions (never their targets) and the instance directory except <c>Saved</c>.</summary>
    Task RemoveJunctionsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Instance delete (plan step 30): with <paramref name="keepWorldData"/> moves <c>ShooterGame\Saved</c> to
    /// <c>Archive\&lt;slug&gt;-&lt;timestamp&gt;</c> and returns that path; otherwise deletes it and returns null.
    /// Removes the now-empty instance directory either way.
    /// </summary>
    Task<string?> RetireAsync(string slug, bool keepWorldData, CancellationToken cancellationToken);
}

/// <summary>Who owns an INI source document: exactly one of the ids is set.</summary>
public sealed record IniOwner(int? ClusterId, int? InstanceId)
{
    public static IniOwner ForCluster(int clusterId) => new(clusterId, null);

    public static IniOwner ForInstance(int instanceId) => new(null, instanceId);

    public bool IsCluster => ClusterId is not null;
}

/// <param name="Text">The file text as loaded.</param>
/// <param name="Sha256">Lower-case hex; the optimistic-concurrency token the editor sends back on save.</param>
/// <param name="UpdatedAt">Last write time of the file.</param>
/// <param name="MirrorStale">True when the database mirror does not match the file (a "retry mirror" action is offered).</param>
public sealed record IniSourceDocument(string Text, string Sha256, DateTimeOffset UpdatedAt, bool MirrorStale);

/// <param name="Succeeded">True when the file was written.</param>
/// <param name="Error">Set on rejection, e.g. "changed since you opened it — reload".</param>
/// <param name="NewSha256">The hash of the saved text, which the editor keeps for its next save.</param>
/// <param name="MirrorFailed">The file write succeeded but the database mirror did not; the UI shows a warning with retry.</param>
/// <param name="WrittenText">The text as written: the saved text in the line endings of the file it replaced (see <see cref="Ini.IniLineEndings"/>).</param>
public sealed record IniSaveResult(bool Succeeded, string? Error, string? NewSha256, bool MirrorFailed, string? WrittenText = null)
{
    public static IniSaveResult Saved(string sha256, bool mirrorFailed, string? writtenText = null) => new(true, null, sha256, mirrorFailed, writtenText);

    public static IniSaveResult Rejected(string error) => new(false, error, null, false);
}

/// <summary>
/// Canonical INI source text on disk (<c>Clusters\&lt;slug&gt;\Config\</c> or <c>Instances\&lt;slug&gt;\Config\</c>)
/// with the <c>IniDocuments</c> mirror (plan step 16). Saves are serialized per document, use optimistic
/// concurrency on the SHA-256, and write temp + atomic rename. The file is authoritative; the mirror
/// exists so "copy the database" is a complete configuration backup.
/// </summary>
public interface IIniSourceStore
{
    /// <summary>Loads the file (empty text with the empty-string hash when it does not exist yet).</summary>
    Task<IniSourceDocument> LoadAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken);

    Task<IniSaveResult> SaveAsync(IniOwner owner, IniFile file, string text, string expectedSha256, CancellationToken cancellationToken);

    /// <summary>Re-writes the mirror row from the file after comparing hashes; the file always wins.</summary>
    Task<IniSaveResult> RetryMirrorAsync(IniOwner owner, IniFile file, CancellationToken cancellationToken);

    /// <summary>The only database → disk direction: rewrites every source file from its mirror row. Explicit, one-time.</summary>
    Task RestoreFromDatabaseAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Writes the generated files for one instance before launch (plan step 16): <c>Game.ini</c> and
/// <c>GameUserSettings.ini</c> under <c>Saved\Config\WindowsServer</c> (temp + rename, one <c>.bak</c> of the
/// previous file) and the admin whitelist. Generated files are never read back as source and are never
/// rewritten on service start or re-attach.
/// </summary>
public interface IGeneratedConfigWriter
{
    /// <summary>Loads the instance, its cluster, mods, overrides, and source texts; generates; writes. Throws on invalid typed values.</summary>
    Task<GeneratedConfig> WriteAsync(int instanceId, CancellationToken cancellationToken);

    /// <summary>The generated <c>GameUserSettings.ini</c> text as it is on disk (the attach path reads RCON credentials from it); null when missing.</summary>
    Task<string?> ReadGeneratedGameUserSettingsAsync(string slug, CancellationToken cancellationToken);
}
