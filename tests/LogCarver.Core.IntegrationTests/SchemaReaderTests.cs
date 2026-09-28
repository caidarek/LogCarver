using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class SchemaReaderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetTableSchemaAsync_MatchesKnownColumnLayout()
    {
        // dbo.TestTable is declared Id INT, Note VARCHAR(200), Amount INT,
        // Bonus INT (added later via ALTER TABLE). Fixed-length columns
        // (Id, Amount, Bonus) are packed consecutively in declaration
        // order in the fixed region; Note, the only variable-length
        // column, is the 1st such column (LeafOffset -1) regardless of
        // where it sits among the fixed ones.
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var schema = await SchemaReader.GetTableSchemaAsync(connection, SqlServerFixture.TableName);

        Assert.Equal(4, schema.Count);

        var id = schema.Single(c => c.Name == "Id");
        Assert.Equal(4, id.LeafOffset); // first fixed column, right after the 4-byte row header
        Assert.Equal(56, id.SystemTypeId); // int

        var note = schema.Single(c => c.Name == "Note");
        Assert.Equal(-1, note.LeafOffset); // 1st variable-length column
        Assert.Equal(167, note.SystemTypeId); // varchar

        var amount = schema.Single(c => c.Name == "Amount");
        Assert.Equal(8, amount.LeafOffset); // 2nd fixed column: right after Id's 4 bytes

        var bonus = schema.Single(c => c.Name == "Bonus");
        Assert.Equal(56, bonus.SystemTypeId);
        // Bonus's own LeafOffset isn't asserted here - it depends on ADD
        // COLUMN placement, which SchemaDriftTests / the end-to-end test
        // cover from the angle that actually matters (old rows must not
        // be decoded using it).
    }

    /// <summary>
    /// Regression test for the bug found during real CLI testing against
    /// SQL Server 2019 on 2026-09-28: sys.partitions has one row per index,
    /// not per table. A heap table with any nonclustered index (here, one
    /// backing a NONCLUSTERED PRIMARY KEY) has two partition rows -
    /// index_id=0 for the heap itself, index_id=2 for the index. Without
    /// filtering to index_id IN (0, 1), the query joined in the index's own
    /// internal column layout alongside the heap's, producing a bogus
    /// SystemTypeId for the column not covered by the index.
    /// </summary>
    [Fact]
    public async Task GetTableSchemaAsync_OnAHeapWithANonclusteredIndex_OnlyReadsTheHeapsOwnColumnLayout()
    {
        const string heapTable = "dbo.HeapWithNonclusteredPk";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{heapTable}') IS NOT NULL DROP TABLE {heapTable}; " +
            $"CREATE TABLE {heapTable} (Id INT NOT NULL, CustomerName NVARCHAR(100), " +
            $"CONSTRAINT PK_HeapWithNonclusteredPk PRIMARY KEY NONCLUSTERED (Id));",
            setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, heapTable);

        Assert.Equal(2, schema.Count);
        var customerName = schema.Single(c => c.Name == "CustomerName");
        Assert.Equal(231, customerName.SystemTypeId); // nvarchar, not the nonclustered index's internal layout
    }

    [Fact]
    public async Task GetTableSchemaAsync_UnknownTable_ReturnsEmpty()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var schema = await SchemaReader.GetTableSchemaAsync(connection, "dbo.NoSuchTable");

        Assert.Empty(schema);
    }
}
