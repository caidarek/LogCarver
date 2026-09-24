using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class TransactionTimeReaderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetTransactionBeginTimesAsync_ResolvesTimesWithinTheWorkloadWindow()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var times = await TransactionTimeReader.GetTransactionBeginTimesAsync(connection);

        Assert.NotEmpty(times);
        // At least one resolved time must fall inside the window the
        // fixture's workload actually ran in - proves the "yyyy/MM/dd
        // HH:mm:ss:fff" format assumption in TransactionTimeReader
        // matches this server's real fn_dblog output, not just a
        // hand-picked sample string.
        Assert.Contains(times.Values, t => t >= fixture.WorkloadStartLocal && t <= fixture.WorkloadEndLocal);
    }
}
