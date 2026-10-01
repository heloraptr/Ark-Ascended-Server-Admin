using System.Text.Json;
using ArkAscendedServerAdmin.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// The SQLite database under <c>DataRoot</c>. Always obtained from <see cref="IDbContextFactory{TContext}"/>
/// — one short-lived context per operation — because singletons (process manager, backup timers) and
/// Blazor circuits use it concurrently.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions _entriesJson = new(JsonSerializerDefaults.Web);

    public DbSet<AppSettingRow> AppSettings => Set<AppSettingRow>();

    public DbSet<Cluster> Clusters => Set<Cluster>();

    public DbSet<Instance> Instances => Set<Instance>();

    public DbSet<Map> Maps => Set<Map>();

    public DbSet<ModLibraryEntry> ModLibrary => Set<ModLibraryEntry>();

    public DbSet<ClusterMod> ClusterMods => Set<ClusterMod>();

    public DbSet<InstanceMod> InstanceMods => Set<InstanceMod>();

    public DbSet<IniDocument> IniDocuments => Set<IniDocument>();

    public DbSet<ExtraOverride> ExtraOverrides => Set<ExtraOverride>();

    public DbSet<KnownPlayer> KnownPlayers => Set<KnownPlayer>();

    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    public DbSet<RestoreRecord> RestoreRecords => Set<RestoreRecord>();

    public DbSet<MaintenanceState> MaintenanceStates => Set<MaintenanceState>();

    public DbSet<ScheduledAction> ScheduledActions => Set<ScheduledAction>();

    public DbSet<ScheduledActionRun> ScheduledActionRuns => Set<ScheduledActionRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppSettingRow>(b =>
        {
            b.ToTable("AppSettings");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(64);
            b.Property(x => x.Value).IsRequired();
        });

        modelBuilder.Entity<Cluster>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            b.Property(x => x.ClusterKey).HasMaxLength(64).IsRequired();
            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.Slug).IsUnique();
            b.OwnsOne(x => x.LaunchFlags, ConfigureLaunchFlags);
            b.Navigation(x => x.LaunchFlags).IsRequired();
        });

        modelBuilder.Entity<Instance>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            b.Property(x => x.SessionName).HasMaxLength(200).IsRequired();
            b.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasOne(x => x.Cluster).WithMany(c => c.Instances).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Map).WithMany().HasForeignKey(x => x.MapId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsOne(x => x.LaunchFlags, ConfigureLaunchFlags);
            b.Navigation(x => x.LaunchFlags).IsRequired();
        });

        modelBuilder.Entity<Map>(b =>
        {
            b.Property(x => x.Key).HasMaxLength(100).IsRequired();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.Key).IsUnique();
            b.Ignore(x => x.TypeLabel);
        });

        modelBuilder.Entity<ModLibraryEntry>(b =>
        {
            b.ToTable("ModLibrary");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Summary).HasMaxLength(2000);
            b.Property(x => x.ThumbnailUrl).HasMaxLength(500);
            b.Property(x => x.WebsiteUrl).HasMaxLength(500);
        });

        modelBuilder.Entity<ClusterMod>(b =>
        {
            b.HasKey(x => new { x.ClusterId, x.ModId });
            b.Property(x => x.Enabled).HasDefaultValue(true);
            b.HasOne(x => x.Cluster).WithMany(c => c.Mods).HasForeignKey(x => x.ClusterId);
            b.HasOne(x => x.Mod).WithMany().HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstanceMod>(b =>
        {
            b.HasKey(x => new { x.InstanceId, x.ModId });
            b.Property(x => x.Enabled).HasDefaultValue(true);
            b.HasOne(x => x.Instance).WithMany(i => i.Mods).HasForeignKey(x => x.InstanceId);
            b.HasOne(x => x.Mod).WithMany().HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IniDocument>(b =>
        {
            b.Property(x => x.File).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            b.Property(x => x.Text).IsRequired();
            b.HasOne(x => x.Cluster).WithMany(c => c.IniDocuments).HasForeignKey(x => x.ClusterId);
            b.HasOne(x => x.Instance).WithMany(i => i.IniDocuments).HasForeignKey(x => x.InstanceId);
            b.HasIndex(x => new { x.ClusterId, x.File }).IsUnique();
            b.HasIndex(x => new { x.InstanceId, x.File }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint(
                "CK_IniDocuments_SingleOwner",
                "(ClusterId IS NULL) <> (InstanceId IS NULL)"));
        });

        modelBuilder.Entity<ExtraOverride>(b =>
        {
            b.Property(x => x.File).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Section).HasMaxLength(200).IsRequired();
            b.Property(x => x.Key).HasMaxLength(200).IsRequired();
            b.Property(x => x.Value).IsRequired();
            b.HasOne(x => x.Instance).WithMany(i => i.ExtraOverrides).HasForeignKey(x => x.InstanceId);
        });

        modelBuilder.Entity<KnownPlayer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.EosId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Platform).HasMaxLength(32);
            b.HasIndex(x => x.EosId).IsUnique();
            b.HasOne(x => x.LastInstance).WithMany().HasForeignKey(x => x.LastInstanceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BackupRecord>(b =>
        {
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.FileName).HasMaxLength(260);
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.HasOne(x => x.Instance).WithMany(i => i.Backups).HasForeignKey(x => x.InstanceId);
            b.HasIndex(x => new { x.InstanceId, x.CreatedAt });
        });

        modelBuilder.Entity<RestoreRecord>(b =>
        {
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.SourceFileName).HasMaxLength(260).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(1000);
            b.HasOne(x => x.Instance).WithMany(i => i.Restores).HasForeignKey(x => x.InstanceId);
            b.HasIndex(x => new { x.InstanceId, x.CreatedAt });
        });

        modelBuilder.Entity<MaintenanceState>(b =>
        {
            b.ToTable("MaintenanceState");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Phase).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Entries)
                .HasConversion(
                    entries => JsonSerializer.Serialize(entries, _entriesJson),
                    json => JsonSerializer.Deserialize<List<MaintenanceEntry>>(json, _entriesJson) ?? new List<MaintenanceEntry>())
                .Metadata.SetValueComparer(new ValueComparer<List<MaintenanceEntry>>(
                    (a, b) => (a ?? new List<MaintenanceEntry>()).SequenceEqual(b ?? new List<MaintenanceEntry>()),
                    v => v.Aggregate(0, (hash, entry) => HashCode.Combine(hash, entry)),
                    v => v.ToList()));
            b.Ignore(x => x.IsResolved);
        });

        modelBuilder.Entity<ScheduledAction>(b =>
        {
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Cron).HasMaxLength(128).IsRequired();
            b.Property(x => x.Command).HasMaxLength(512).IsRequired();
            b.HasOne(x => x.Cluster).WithMany(c => c.ScheduledActions).HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Instance).WithMany(i => i.ScheduledActions).HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
            b.ToTable(t => t.HasCheckConstraint(
                "CK_ScheduledActions_SingleOwner",
                "(ClusterId IS NULL) <> (InstanceId IS NULL)"));
        });

        modelBuilder.Entity<ScheduledActionRun>(b =>
        {
            b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            b.Property(x => x.Reason).HasMaxLength(512).IsRequired();
            b.HasOne(x => x.ScheduledAction).WithMany(a => a.Runs).HasForeignKey(x => x.ScheduledActionId);
            b.HasOne(x => x.Instance).WithMany().HasForeignKey(x => x.InstanceId);
            b.HasIndex(x => new { x.ScheduledActionId, x.InstanceId, x.ScheduledFor }).IsUnique();
            b.HasIndex(x => new { x.InstanceId, x.StartedAt });
        });
    }

    private static void ConfigureLaunchFlags<TOwner>(Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<TOwner, LaunchFlags> b)
        where TOwner : class
    {
        b.Property(x => x.ServerPlatform).HasMaxLength(64);
        b.Property(x => x.ActiveEvent).HasMaxLength(64);
        b.Property(x => x.AdditionalArgs).HasMaxLength(4000);
    }
}

/// <summary>One App Setting; see <see cref="Configuration.AppSettingsCodec"/> for the key list.</summary>
public sealed class AppSettingRow
{
    public required string Key { get; set; }

    public required string Value { get; set; }
}
