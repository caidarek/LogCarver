using LogCarver.Core;
using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServerEdgeCases")]
public class LobHandlingTests(EdgeCaseFixture fixture)
{
    [Fact]
    public async Task InRowLobDecodesCleanly_OffRowLobThrowsAClearException()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var schema = await SchemaReader.GetTableSchemaAsync(connection, EdgeCaseFixture.LobTableName);
        var records = await FnDblogReader.ReadClusteredRecordsAsync(connection, EdgeCaseFixture.LobTableName);
        var inserts = records.Where(r => r.Operation == "LOP_INSERT_ROWS").ToList();

        Assert.Equal(2, inserts.Count); // one in-row insert, one off-row insert

        // Decode each independently (never inside a LINQ predicate that
        // could itself throw mid-evaluation) and classify by outcome
        // rather than guessing which record is which from byte length.
        var outcomes = inserts.Select(r =>
        {
            try
            {
                return (Decoded: (IReadOnlyDictionary<string, object?>?)RowDecoder.Decode(r.RowLogContents0!, schema), Error: (Exception?)null);
            }
            catch (Exception ex)
            {
                return (Decoded: (IReadOnlyDictionary<string, object?>?)null, Error: ex);
            }
        }).ToList();

        var succeeded = outcomes.Where(o => o.Error is null).ToList();
        var failed = outcomes.Where(o => o.Error is not null).ToList();

        Assert.Single(succeeded);
        Assert.Equal(EdgeCaseFixture.InRowValue, succeeded[0].Decoded!["SmallLob"]);

        Assert.Single(failed);
        Assert.IsType<UnsupportedRowFormatException>(failed[0].Error);
    }
}
