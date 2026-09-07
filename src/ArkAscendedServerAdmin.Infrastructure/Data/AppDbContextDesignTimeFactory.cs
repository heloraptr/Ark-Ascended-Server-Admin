using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArkAscendedServerAdmin.Infrastructure.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without starting the host:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/ArkAscendedServerAdmin.Infrastructure --startup-project src/ArkAscendedServerAdmin.Server</c>.
/// The connection string is never opened at design time, so no file is created.
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new AppDbContext(options);
    }
}
