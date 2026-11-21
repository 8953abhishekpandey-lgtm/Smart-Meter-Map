using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace SmartMeter.Infrastructure.Persistence;

public interface ISqlConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct);
    string ConnectionString { get; }
}

internal sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    public SqlConnectionFactory(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("SmartMeterDb")
                  ?? throw new InvalidOperationException("ConnectionStrings:SmartMeterDb is not configured.");
        var builder = new SqlConnectionStringBuilder(raw);
        if (string.IsNullOrEmpty(builder.ApplicationName) || builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
            builder.ApplicationName = "SmartMeter.Api";
        ConnectionString = builder.ConnectionString;
    }

    public string ConnectionString { get; }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}

/// <summary>Dapper type handlers for DateOnly (SQL 'date').</summary>
internal static class DapperSetup
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
        SqlMapper.AddTypeHandler(new NullableDateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private sealed class NullableDateOnlyHandler : SqlMapper.TypeHandler<DateOnly?>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly? value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value is { } d ? d.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        }

        public override DateOnly? Parse(object value) => value switch
        {
            null or DBNull => null,
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => DateOnly.Parse(value.ToString()!, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
