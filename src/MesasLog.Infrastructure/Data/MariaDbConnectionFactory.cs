using MesasLog.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace MesasLog.Infrastructure.Data;

public sealed class MariaDbConnectionFactory(
    IOptions<MesasLogOptions> options,
    ILogger<MariaDbConnectionFactory> logger)
{
    private readonly MesasLogOptions _opt = options.Value;

    /// <summary>Nome do banco configurado em appsettings (ex.: mesas_log).</summary>
    public string DatabaseName => _opt.Database.Database;

    public string ConnectionString => new MySqlConnectionStringBuilder
    {
        Server = _opt.Database.Host,
        Port = (uint)_opt.Database.Port,
        Database = _opt.Database.Database,
        UserID = _opt.Database.User,
        Password = _opt.Database.Password,
        CharacterSet = "utf8mb4"
    }.ConnectionString;

    /// <summary>Conexão apenas ao servidor (sem catalog), para CREATE DATABASE.</summary>
    public string ServerOnlyConnectionString => new MySqlConnectionStringBuilder
    {
        Server = _opt.Database.Host,
        Port = (uint)_opt.Database.Port,
        UserID = _opt.Database.User,
        Password = _opt.Database.Password,
        CharacterSet = "utf8mb4"
    }.ConnectionString;

    public MySqlConnection CreateConnection() => new(ConnectionString);

    public MySqlConnection CreateServerConnection() => new(ServerOnlyConnectionString);

    /// <summary>
    /// Abre a conexão e desativa o binlog nesta sessão, para que INSERT/UPDATE/DELETE do log
    /// não aumentem os ficheiros de binlog do servidor (evita ciclo de releitura e crescimento).
    /// </summary>
    public async Task<MySqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await TryDisableSessionBinlogAsync(conn, ct);
        return conn;
    }

    private async Task TryDisableSessionBinlogAsync(MySqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SET SESSION sql_log_bin = 0";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Não foi possível executar SET SESSION sql_log_bin = 0. Os escritos da app podem gerar eventos no binlog do servidor. " +
                "Garanta privilégio adequado (ex.: SUPER ou BINLOG ADMIN) ou ignore se o destino não for o mesmo servidor dos ficheiros lidos.");
        }
    }
}
