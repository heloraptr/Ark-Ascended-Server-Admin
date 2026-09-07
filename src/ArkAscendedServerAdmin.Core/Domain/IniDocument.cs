namespace ArkAscendedServerAdmin.Domain;

/// <summary>
/// Database mirror of one canonical INI source file (<c>Clusters\&lt;slug&gt;\Config\</c> or
/// <c>Instances\&lt;slug&gt;\Config\</c>). The file on disk is authoritative; this row exists so "copy the
/// database" is a complete configuration backup and "restore from database" can rewrite the files.
/// Exactly one of <see cref="ClusterId"/> / <see cref="InstanceId"/> is set.
/// </summary>
public sealed class IniDocument
{
    public int Id { get; set; }

    public int? ClusterId { get; set; }

    public Cluster? Cluster { get; set; }

    public int? InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public IniFile File { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of <see cref="Text"/>, the optimistic-concurrency token.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A single per-instance <c>[Section] Key=Value</c> applied on top of the source INI text.</summary>
public sealed class ExtraOverride
{
    public int Id { get; set; }

    public int InstanceId { get; set; }

    public Instance? Instance { get; set; }

    public IniFile File { get; set; }

    public required string Section { get; set; }

    public required string Key { get; set; }

    public string Value { get; set; } = string.Empty;
}
