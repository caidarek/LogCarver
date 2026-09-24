using LogCarver.Core.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace LogCarver.Core.IntegrationTests;

[Collection("SqlServer")]
public class DdlBoundaryReaderTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task GetDdlBoundariesAsync_DetectsTheAddColumnDdl()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, SqlServerFixture.TableName);

        Assert.NotEmpty(boundaries);
        Assert.Contains(boundaries, b => b.TransactionName == "ALTER TABLE");
    }

    [Fact]
    public async Task GetDdlBoundariesAsync_TableThatWasNeverAltered_ReturnsCreateOnly()
    {
        string tableName = "dbo.NeverAltered";
        await using var setup = new SqlConnection(fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('{tableName}') IS NOT NULL DROP TABLE {tableName}; CREATE TABLE {tableName} (Id INT NOT NULL PRIMARY KEY);", setup))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var boundaries = await DdlBoundaryReader.GetDdlBoundariesAsync(connection, tableName);

        Assert.DoesNotContain(boundaries, b => b.TransactionName == "ALTER TABLE");
    }
}
