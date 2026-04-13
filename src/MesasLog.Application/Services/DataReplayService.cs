using System.Text.RegularExpressions;
using MesasLog.Core;
using MesasLog.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace MesasLog.Application.Services;

/// <summary>
/// Reconstrói tabelas com prefixo log_ aplicando eventos armazenados (idempotente: trunca e reaplica).
/// </summary>
public sealed partial class DataReplayService(
    MariaDbConnectionFactory factory,
    BinlogEventRepository eventRepository,
    IOptions<MesasLogOptions> options,
    ILogger<DataReplayService> logger)
{
    public async Task<ReplayResult> ReplayFromLogAsync(bool dryRun, CancellationToken ct)
    {
        var steps = new List<string>();
        await using var conn = await factory.OpenConnectionAsync(ct);

        var logTables = await ListLogTablesAsync(conn, ct);
        if (logTables.Count == 0)
            return new ReplayResult(false, "Nenhuma tabela log_* encontrada.", steps);

        if (!dryRun)
        {
            foreach (var t in logTables)
            {
                await using var trunc = conn.CreateCommand();
                trunc.CommandText = $"TRUNCATE TABLE `{t}`";
                await trunc.ExecuteNonQueryAsync(ct);
                steps.Add($"Truncada {t}");
            }
        }
        else
        {
            steps.Add("[dry-run] Não truncar tabelas.");
        }

        var events = await eventRepository.GetAllEventsOrderedAsync(ct);
        steps.Add($"Total de eventos no log: {events.Count}");

        var insertMode = options.Value.Processing.InsertConflict;
        var applied = 0;
        foreach (var row in events)
        {
            ct.ThrowIfCancellationRequested();
            var sql = row.QuerySql;
            if (string.IsNullOrWhiteSpace(sql)) continue;

            var rewritten = RewriteSqlForLogDatabase(sql, insertMode);
            if (rewritten == null) continue;

            if (dryRun)
            {
                applied++;
                continue;
            }

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = rewritten;
                await cmd.ExecuteNonQueryAsync(ct);
                applied++;
            }
            catch (MySqlException ex)
            {
                logger.LogWarning(ex, "SQL replay ignorado: {Sql}", rewritten[..Math.Min(120, rewritten.Length)]);
            }
        }

        steps.Add($"Eventos aplicados: {applied}.");
        return new ReplayResult(true, null, steps);
    }

    private static async Task<List<string>> ListLogTablesAsync(MySqlConnection conn, CancellationToken ct)
    {
        var list = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME LIKE 'log\\_%'";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(r.GetString(0));
        return list;
    }

    private static string? RewriteSqlForLogDatabase(string sql, InsertConflictBehavior insertMode)
    {
        var s = BacktickPairRegex().Replace(sql.Trim(), m =>
        {
            var db = m.Groups[1].Value;
            var tbl = m.Groups[2].Value;
            if (tbl.StartsWith("log_", StringComparison.OrdinalIgnoreCase))
                return m.Value;
            return $"`{db}`.`log_{tbl}`";
        });

        if (!s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            return s;

        return insertMode switch
        {
            InsertConflictBehavior.Ignore => InsertRegex().Replace(s, "INSERT IGNORE ", 1),
            InsertConflictBehavior.Overwrite => InsertRegex().Replace(s, "REPLACE ", 1),
            _ => s
        };
    }

    [GeneratedRegex("^INSERT\\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertRegex();

    [GeneratedRegex(@"`([^`]+)`\.`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickPairRegex();
}

public sealed record ReplayResult(bool Success, string? ErrorMessage, IReadOnlyList<string> Steps);
