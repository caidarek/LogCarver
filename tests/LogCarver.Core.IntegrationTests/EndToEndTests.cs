using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

/// <summary>
/// Exercises the exact same call sequence LogCarver.Cli's Program.cs uses,
/// against a real database, so a break in how the pieces wire together
/// (not just each piece in isolation) gets caught by `dotnet test`
/// instead of only by manually running the Cli.
/// </summary>
[Collection("SqlServer")]
public class EndToEndTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task FullPipeline_ReconstructsHistory_AndRefusesPreDdlRecordsForTheAddedColumn()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var schema = await SchemaReader.GetTableSchemaAsync(connection, SqlServerFixture.TableName);
        var ddlBoundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, SqlServerFixture.TableName);
        var ddlBoundaryLsns = ddlBoundaries.Select(b => b.Lsn).ToList();
        var transactionTimes = await TransactionTimeReader.GetTransactionBeginTimesAsync(connection);
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, SqlServerFixture.TableName);

        var history = RowHistoryReconstructor.Reconstruct(records, schema, ddlBoundaryLsns, transactionTimes);

        // Row 1: inserted pre-DDL, then updated pre-DDL. The current
        // schema includes the later-added Bonus column, so both the
        // insert and the update must be refused, not decoded with a
        // fabricated Bonus value.
        Assert.Contains(history, e => e.Kind == RowEventKind.Insert && e.Note != null && e.Note.Contains("schema-changing DDL"));
        Assert.Contains(history, e => e.Kind == RowEventKind.Update && e.Note != null && e.Note.Contains("schema-changing DDL"));

        // Row 2: inserted then deleted, also pre-DDL - same refusal applies to the delete.
        Assert.Contains(history, e => e.Kind == RowEventKind.Delete && e.Note != null && e.Note.Contains("schema-changing DDL"));

        // Row 3: inserted after the DDL, must decode cleanly with the real Bonus value.
        var row3Insert = history.Single(e => e.Kind == RowEventKind.Insert && e.After != null && (int)e.After["Id"]! == SqlServerFixture.Row3IdPostDdl);
        Assert.Equal(SqlServerFixture.Row3Bonus, row3Insert.After!["Bonus"]);
        Assert.Equal("post-ddl-row", row3Insert.After["Note"]);

        // Every event's timestamp, when resolved, falls inside the workload window.
        Assert.All(history.Where(e => e.Timestamp is not null),
            e => Assert.InRange(e.Timestamp!.Value, fixture.WorkloadStartLocal, fixture.WorkloadEndLocal));
    }
}
