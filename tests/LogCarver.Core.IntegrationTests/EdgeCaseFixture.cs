using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

/// <summary>
/// Sets up the "known limitation" scenarios (off-row LOB, compressed
/// tables) in their own sandbox database, separate from SqlServerFixture's
/// workload so these edge cases can't interfere with the main pipeline
/// tests. Same safety convention: only ever touches databases prefixed
/// "LogCarver_".
/// </summary>
public sealed class EdgeCaseFixture : IAsyncLifetime
{
    private const string DatabaseName = "LogCarver_EdgeCases";
    private const string MasterConnectionString = "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;";

    public string ConnectionString { get; } =
        $"Server=localhost;Database={DatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    public const string LobTableName = "dbo.LobTable";
    public const int InRowId = 1;
    public const string InRowValue = "in-row-small-value";
    public const int OffRowId = 2;
    public const int OffRowValueLength = 100_000; // comfortably over the ~8000-byte in-row threshold

    public const string CompressedTableName = "dbo.CompressedTable";
    public const string UncompressedTableName = "dbo.UncompressedTable";

    public async Task InitializeAsync()
    {
        AssertSafeDatabaseName(DatabaseName);

        await using (var master = new SqlConnection(MasterConnectionString))
        {
            await master.OpenAsync();
            await DropIfExistsAsync(master);
            await ExecAsync(master, $"CREATE DATABASE [{DatabaseName}];");
        }

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await ExecAsync(conn, $"CREATE TABLE {LobTableName} (Id INT NOT NULL PRIMARY KEY, SmallLob VARCHAR(MAX) NULL);");
        await ExecAsync(conn, $"INSERT INTO {LobTableName} VALUES ({InRowId}, '{InRowValue}');");
        await ExecAsync(conn, $"INSERT INTO {LobTableName} VALUES ({OffRowId}, REPLICATE('x', {OffRowValueLength}));");

        await ExecAsync(conn, $"CREATE TABLE {CompressedTableName} (Id INT NOT NULL PRIMARY KEY, Note VARCHAR(50) NULL) WITH (DATA_COMPRESSION = ROW);");
        await ExecAsync(conn, $"INSERT INTO {CompressedTableName} VALUES (1, 'a-row');");

        await ExecAsync(conn, $"CREATE TABLE {UncompressedTableName} (Id INT NOT NULL PRIMARY KEY);");
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();
        await DropIfExistsAsync(master);
    }

    private static async Task DropIfExistsAsync(SqlConnection master)
    {
        AssertSafeDatabaseName(DatabaseName);
        await ExecAsync(master, $"""
            IF DB_ID('{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{DatabaseName}];
            END
            """);
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static void AssertSafeDatabaseName(string name)
    {
        if (!name.StartsWith("LogCarver_", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refusing to operate on database '{name}': integration tests may only touch databases prefixed 'LogCarver_'.");
    }
}

[CollectionDefinition("SqlServerEdgeCases")]
public sealed class EdgeCaseCollection : ICollectionFixture<EdgeCaseFixture>;
