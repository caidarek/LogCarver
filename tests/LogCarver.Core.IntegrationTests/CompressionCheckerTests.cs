using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServerEdgeCases")]
public class CompressionCheckerTests(EdgeCaseFixture fixture)
{
    [Fact]
    public async Task RowCompressedTable_IsDetectedAsCompressed()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var types = await CompressionChecker.GetCompressionTypesAsync(connection, EdgeCaseFixture.CompressedTableName);

        Assert.True(CompressionChecker.IsCompressed(types));
        Assert.Contains("ROW", types);
    }

    [Fact]
    public async Task UncompressedTable_IsNotDetectedAsCompressed()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var types = await CompressionChecker.GetCompressionTypesAsync(connection, EdgeCaseFixture.UncompressedTableName);

        Assert.False(CompressionChecker.IsCompressed(types));
    }
}
