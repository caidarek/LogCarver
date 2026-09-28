using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// fn_dblog's exact behavior (column set, fn_dump_dblog's parameter count,
/// which DMVs exist) is not guaranteed consistent across SQL Server
/// versions - this is undocumented surface area, confirmed to differ in
/// practice during the research phase (研究紀錄 第五節). Never assume an
/// unvalidated version behaves like a validated one; warn instead.
/// </summary>
public static class SqlServerVersion
{
    /// <summary>Major.Minor product versions this decoder has actually been run against.</summary>
    public static readonly IReadOnlyList<string> ValidatedVersions = ["17.0", "16.0", "15.0", "13.0"]; // SQL Server 2025, 2022, 2019, 2016

    public static async Task<VersionCheckResult> CheckAsync(SqlConnection connection, CancellationToken ct = default)
    {
        await using var command = new SqlCommand(
            "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128));", connection);
        var raw = (string)(await command.ExecuteScalarAsync(ct))!;
        var parts = raw.Split('.');
        var majorMinor = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : raw;
        return new VersionCheckResult(raw, majorMinor, ValidatedVersions.Contains(majorMinor));
    }
}

public sealed record VersionCheckResult(string ProductVersion, string MajorMinor, bool IsValidated);
