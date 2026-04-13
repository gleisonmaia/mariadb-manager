using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MesasLog.Infrastructure.Data;

public sealed class SchemaInitializer(
    MariaDbConnectionFactory factory,
    ILogger<SchemaInitializer> logger)
{
    private static readonly Regex CreateTableNameRegex = new(
        @"CREATE\s+TABLE\s+(?<q>`?)(?<name>\w+)\k<q>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>
    /// Garante que o banco configurado exista (CREATE DATABASE IF NOT EXISTS).
    /// Exige permissão global CREATE no MariaDB para o usuário da aplicação.
    /// </summary>
    public async Task EnsureDatabaseExistsAsync(CancellationToken ct = default)
    {
        var name = factory.DatabaseName.Trim();
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("MesasLog:Database:Database não pode ser vazio.");

        if (HasDangerousIdentifierChars(name))
            throw new InvalidOperationException($"Nome de banco contém caracteres não permitidos: '{name}'.");

        var escaped = name.Replace("`", "``", StringComparison.Ordinal);
        await using var conn = factory.CreateServerConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE DATABASE IF NOT EXISTS `{escaped}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Banco '{Database}' verificado ou criado.", name);
    }

    public async Task EnsureCoreSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS binlog_checkpoint (
              id INT PRIMARY KEY,
              binlog_file VARCHAR(512) NOT NULL,
              binlog_position BIGINT NOT NULL,
              updated_at DATETIME(3) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

            CREATE TABLE IF NOT EXISTS binlog_event_log (
              id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
              data_evento DATETIME(3) NOT NULL,
              banco VARCHAR(128) NOT NULL,
              tabela VARCHAR(128) NOT NULL,
              tipo_operacao VARCHAR(16) NOT NULL,
              dados_antes JSON NULL,
              dados_depois JSON NULL,
              query_sql LONGTEXT NULL,
              binlog_file VARCHAR(512) NOT NULL,
              binlog_position BIGINT NOT NULL,
              UNIQUE KEY uk_binlog_event (binlog_file(180), binlog_position, banco, tabela),
              KEY ix_data_evento (data_evento),
              KEY ix_banco (banco),
              KEY ix_tabela (tabela)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("Esquema principal (checkpoint + binlog_event_log) verificado.");
    }

    public async Task ApplyMesasSnapshotAsync(string mesasSqlPath, CancellationToken ct = default)
    {
        if (!File.Exists(mesasSqlPath))
        {
            logger.LogWarning("Arquivo mesas.sql não encontrado em {Path}", mesasSqlPath);
            return;
        }

        var sql = await File.ReadAllTextAsync(mesasSqlPath, ct);
        await ApplyMesasSnapshotFromContentAsync(sql, ct);
    }

    public async Task ApplyMesasSnapshotFromContentAsync(string sqlContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sqlContent))
            return;

        await using var conn = await factory.OpenConnectionAsync(ct);

        foreach (var stmt in SplitCreateTableStatements(sqlContent))
        {
            var prefixed = AddLogPrefixToCreateTable(stmt);
            if (string.IsNullOrWhiteSpace(prefixed)) continue;
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = prefixed;
                await cmd.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Snapshot aplicado: {Snippet}", prefixed[..Math.Min(80, prefixed.Length)]);
            }
            catch (MySqlException ex) when (IsTableAlreadyExists(ex))
            {
                logger.LogDebug("CREATE TABLE omitido — tabela já existe: {Snippet}",
                    prefixed[..Math.Min(60, prefixed.Length)]);
            }
            catch (MySqlException ex)
            {
                logger.LogWarning(ex, "CREATE TABLE ignorado ou erro: {Snippet}", prefixed[..Math.Min(60, prefixed.Length)]);
            }
        }
    }

    private static IEnumerable<string> SplitCreateTableStatements(string fileContent)
    {
        var parts = fileContent.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
                yield return p;
        }
    }

    /// <summary>1050 = ER_TABLE_EXISTS_ERROR (MySQL/MariaDB); 42S01 = SQLSTATE equivalente.</summary>
    private static bool IsTableAlreadyExists(MySqlException ex) =>
        ex.Number == 1050 || string.Equals(ex.SqlState, "42S01", StringComparison.Ordinal);

    private static bool HasDangerousIdentifierChars(string name)
    {
        foreach (var c in name)
        {
            if (c is ';' or '\0' or '\r' or '\n' or '\u0085' or '\u2028' or '\u2029')
                return true;
        }

        return false;
    }

    private static string AddLogPrefixToCreateTable(string createStatement)
    {
        return CreateTableNameRegex.Replace(createStatement, m =>
        {
            var name = m.Groups["name"].Value;
            var q = m.Groups["q"].Value;
            if (name.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                return m.Value;
            return $"CREATE TABLE {q}log_{name}{q}";
        }, 1);
    }
}
