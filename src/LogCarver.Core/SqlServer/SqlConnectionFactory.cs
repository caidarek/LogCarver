using Microsoft.Data.SqlClient;

namespace LogCarver.Core.SqlServer;

/// <summary>
/// Single place that builds SQL Server connection strings, so the
/// TrustServerCertificate trade-off (see remarks) and the choice between
/// Windows and SQL authentication aren't each reimplemented - and
/// potentially gotten wrong or forgotten - at every call site across
/// LogCarver.Cli, LogCarverOffline.Cli and LogCarverOffline.Web.
/// </summary>
public static class SqlConnectionFactory
{
    /// <remarks>
    /// TrustServerCertificate=true is a pragmatic default for local/dev SQL
    /// Server instances with self-signed certs, matching how the research
    /// phase worked around the same issue (sqlcmd -C). It disables TLS
    /// certificate validation, so a network-level attacker could intercept
    /// the connection; a security-conscious deployment should make this
    /// configurable instead of always trusting the server certificate.
    /// </remarks>
    public static string BuildConnectionString(string server, string database, string? user = null, string? password = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            TrustServerCertificate = true,
        };
        if (user is not null)
        {
            builder.UserID = user;
            builder.Password = password;
        }
        else
        {
            builder.IntegratedSecurity = true;
        }
        return builder.ConnectionString;
    }
}
