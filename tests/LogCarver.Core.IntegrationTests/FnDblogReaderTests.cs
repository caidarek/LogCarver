using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class FnDblogReaderTests(SqlServerFixture fixture)
{
    /// <summary>
    /// Regression test for the bug found during CLI testing on 2026-09-23:
    /// FnDblogReader's query originally filtered to
    /// Context='LCX_CLUSTERED' only, which silently excluded every
    /// DELETE (DELETE uses Context='LCX_MARK_AS_GHOST'). Change the SQL
    /// in FnDblogReader.cs back to that single-context filter and this
    /// test must fail.
    /// </summary>
    [Fact]
    public async Task ReadClusteredRecordsAsync_ReturnsAllThreeOperationTypes()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        Assert.Contains(records, r => r.Operation == "LOP_INSERT_ROWS");
        Assert.Contains(records, r => r.Operation is "LOP_MODIFY_ROW" or "LOP_MODIFY_COLUMNS");
        Assert.Contains(records, r => r.Operation == "LOP_DELETE_ROWS");
    }

    [Fact]
    public async Task ReadClusteredRecordsAsync_RecordsCarryPageAndSlotForRowIdentity()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        Assert.All(records, r =>
        {
            Assert.NotNull(r.PageId);
            Assert.NotNull(r.SlotId);
        });
    }

    [Fact]
    public async Task ReadClusteredRecordsAsync_DoesNotMatchUnrelatedTableWithSharedPrefix()
    {
        // Guards against the AllocUnitName LIKE '%TableName%' mistake from
        // earlier in the research phase, which cross-matched e.g.
        // LogTestAlterType when searching for LogTest. dbo.TestTableExtra
        // shares "dbo.TestTable" as a literal prefix but is a distinct table.
        string extraTable = $"{SqlServerFixture.TableName}Extra";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{extraTable}') IS NOT NULL DROP TABLE {extraTable}; " +
            $"CREATE TABLE {extraTable} (Id INT NOT NULL PRIMARY KEY); " +
            $"INSERT INTO {extraTable} VALUES (999); INSERT INTO {extraTable} VALUES (998);", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        // TestTable only ever had 3 inserts (Id 1, 2, 3). If the query
        // matched by "AllocUnitName LIKE '%TestTable%'" instead of the
        // precise "TestTable.%" prefix, TestTableExtra's 2 inserts would
        // leak in and this count would be 5, not 3.
        Assert.Equal(3, records.Count(r => r.Operation == "LOP_INSERT_ROWS"));
    }
}
