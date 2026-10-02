using ArkAscendedServerAdmin.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace ArkAscendedServerAdmin.Infrastructure.IntegrationTests.Data;

public class SqliteConnectionStringsTests
{
    [Theory]
    [InlineData(@"C:\Ark;Mode=Memory\Data\ark.db")]
    [InlineData(@"C:\Ark=Root\Data\ark.db")]
    [InlineData(@"C:\Ark; Cache=Shared; Data Source=other.db\ark.db")]
    public void ForFile_KeepsSemicolonsAndEqualsSignsInsideTheDataSource(string path)
    {
        var parsed = new SqliteConnectionStringBuilder(SqliteConnectionStrings.ForFile(path));

        Assert.Equal(path, parsed.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, parsed.Mode);
        Assert.Equal(SqliteCacheMode.Default, parsed.Cache);
    }
}
