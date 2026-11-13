using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace MeterMap.Data;

public sealed class SqlConnectionFactory(IOptionsMonitor<DatabaseOptions> options)
{
    public string ActiveName => options.CurrentValue.ActiveProfile;

    public DbProfile Profile(string? name = null)
    {
        var o = options.CurrentValue;
        var key = name ?? o.ActiveProfile;
        if (!o.Profiles.TryGetValue(key, out var p))
            throw new InvalidOperationException($"Profile '{key}'not found in appsettings .");
        return p;
    }

    public SqlConnection Create(string? name = null)
    {
        var p = Profile(name);

        var b = new SqlConnectionStringBuilder
        {
            DataSource = p.Server,
            InitialCatalog = p.Database,
            TrustServerCertificate = true,
            ApplicationName = "MeterMap",
            ConnectTimeout = 15
        };

        var mode = (p.AuthMode ?? "").Trim();

        bool isSql =
            mode.Equals("Sql", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("SQL Server Authentication", StringComparison.OrdinalIgnoreCase);

        bool isWindows =
            mode.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
            mode.Equals("Windows Authentication", StringComparison.OrdinalIgnoreCase);

        if (isSql)
        {
            if (string.IsNullOrWhiteSpace(p.User) || string.IsNullOrWhiteSpace(p.Password))
            {
                throw new InvalidOperationException("SQL Authentication requires User and Password.");
            }

            b.UserID = p.User;
            b.Password = p.Password;
        }
        else if (isWindows)
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported authentication mode: {p.AuthMode}");
        }

        return new SqlConnection(b.ConnectionString);
    }
}