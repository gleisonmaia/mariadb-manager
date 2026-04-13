using System.Text;
using System.Text.Json;
using MesasLog.Core;
using MesasLog.Infrastructure.Naming;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MesasLog.Infrastructure.Data;

public sealed class BinlogEventRepository(MariaDbConnectionFactory factory, ILogger<BinlogEventRepository> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await factory.OpenConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao conectar ao banco de destino.");
            return false;
        }
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM binlog_event_log WHERE data_evento < @c";
        cmd.Parameters.AddWithValue("@c", cutoffUtc);
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) logger.LogInformation("Removidos {N} registros de log anteriores a {Cutoff}", n, cutoffUtc);
        return n;
    }

    public async Task InsertBatchAsync(IReadOnlyList<BinlogParsedRowEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            foreach (var e in events)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (MySqlTransaction)tx;
                cmd.CommandText = """
                    INSERT INTO binlog_event_log
                    (data_evento, banco, tabela, tipo_operacao, dados_antes, dados_depois, query_sql, binlog_file, binlog_position)
                    VALUES (@de, @b, @t, @tipo, @antes, @depois, @q, @bf, @bp)
                    ON DUPLICATE KEY UPDATE id = id
                    """;
                var dataEvt = e.EventTimestamp ?? DateTime.UtcNow;
                var b = IdentifierNormalizer.NormalizeDatabase(e.Database);
                var t = IdentifierNormalizer.NormalizeTable(e.Table);
                cmd.Parameters.AddWithValue("@de", dataEvt);
                cmd.Parameters.AddWithValue("@b", b);
                cmd.Parameters.AddWithValue("@t", t);
                cmd.Parameters.AddWithValue("@tipo", e.Operation.ToString());
                cmd.Parameters.AddWithValue("@antes", e.Before == null ? null : JsonSerializer.Serialize(e.Before, JsonOpts));
                cmd.Parameters.AddWithValue("@depois", e.After == null ? null : JsonSerializer.Serialize(e.After, JsonOpts));
                cmd.Parameters.AddWithValue("@q", (object?)e.QuerySql ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@bf", e.BinlogFile);
                cmd.Parameters.AddWithValue("@bp", e.BinlogPosition);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<PagedResult<BinlogEventLogRow>> QueryAsync(EventLogQuery q, CancellationToken ct = default)
    {
        var (whereSql, bindWhere) = BuildWhereClause(q);
        var orderCol = q.OrderBy.ToLowerInvariant() switch
        {
            "banco" => "banco",
            "tabela" => "tabela",
            _ => "data_evento"
        };
        var dir = q.OrderDescending ? "DESC" : "ASC";
        var offset = Math.Max(0, (q.Page - 1) * q.PageSize);

        await using var conn = await factory.OpenConnectionAsync(ct);

        int total;
        await using (var countCmd = conn.CreateCommand())
        {
            bindWhere(countCmd);
            countCmd.CommandText = $"SELECT COUNT(*) FROM binlog_event_log WHERE {whereSql}";
            total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        }

        var items = new List<BinlogEventLogRow>();
        await using (var selCmd = conn.CreateCommand())
        {
            bindWhere(selCmd);
            selCmd.CommandText = $"""
                SELECT id, data_evento, banco, tabela, tipo_operacao, dados_antes, dados_depois, query_sql, binlog_file, binlog_position
                FROM binlog_event_log
                WHERE {whereSql}
                ORDER BY {orderCol} {dir}, id {dir}
                LIMIT @lim OFFSET @off
                """;
            selCmd.Parameters.AddWithValue("@lim", q.PageSize);
            selCmd.Parameters.AddWithValue("@off", offset);

            await using var r = await selCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new BinlogEventLogRow
                {
                    Id = r.GetInt64(0),
                    DataEvento = r.GetDateTime(1),
                    Banco = r.GetString(2),
                    Tabela = r.GetString(3),
                    TipoOperacao = Enum.Parse<TipoOperacao>(r.GetString(4), true),
                    DadosAntesJson = r.IsDBNull(5) ? null : r.GetString(5),
                    DadosDepoisJson = r.IsDBNull(6) ? null : r.GetString(6),
                    QuerySql = r.IsDBNull(7) ? null : r.GetString(7),
                    BinlogFile = r.GetString(8),
                    BinlogPosition = r.GetInt64(9)
                });
            }
        }

        return new PagedResult<BinlogEventLogRow>
        {
            Items = items,
            TotalCount = total,
            Page = q.Page,
            PageSize = q.PageSize
        };
    }

    public async Task<IReadOnlyList<BinlogEventLogRow>> GetAllEventsOrderedAsync(CancellationToken ct = default)
    {
        var items = new List<BinlogEventLogRow>();
        await using var conn = await factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, data_evento, banco, tabela, tipo_operacao, dados_antes, dados_depois, query_sql, binlog_file, binlog_position
            FROM binlog_event_log
            ORDER BY data_evento ASC, id ASC
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(new BinlogEventLogRow
            {
                Id = r.GetInt64(0),
                DataEvento = r.GetDateTime(1),
                Banco = r.GetString(2),
                Tabela = r.GetString(3),
                TipoOperacao = Enum.Parse<TipoOperacao>(r.GetString(4), true),
                DadosAntesJson = r.IsDBNull(5) ? null : r.GetString(5),
                DadosDepoisJson = r.IsDBNull(6) ? null : r.GetString(6),
                QuerySql = r.IsDBNull(7) ? null : r.GetString(7),
                BinlogFile = r.GetString(8),
                BinlogPosition = r.GetInt64(9)
            });
        }

        return items;
    }

    private static (string WhereSql, Action<MySqlCommand> BindWhere) BuildWhereClause(EventLogQuery q)
    {
        var sb = new StringBuilder("1=1");

        void BindWhere(MySqlCommand cmd)
        {
            cmd.Parameters.Clear();
            if (!string.IsNullOrWhiteSpace(q.Banco))
                cmd.Parameters.AddWithValue("@banco", q.Banco!.Trim());
            if (!string.IsNullOrWhiteSpace(q.Tabela))
                cmd.Parameters.AddWithValue("@tab", "%" + q.Tabela!.Trim() + "%");
            if (q.TipoOperacao is { } tipo)
                cmd.Parameters.AddWithValue("@tipo", tipo.ToString());
            if (q.DataInicio is { } di)
                cmd.Parameters.AddWithValue("@di", di);
            if (q.DataFim is { } df)
                cmd.Parameters.AddWithValue("@df", df);
            if (!string.IsNullOrWhiteSpace(q.TextoQuerySql))
                cmd.Parameters.AddWithValue("@qs", "%" + q.TextoQuerySql!.Trim() + "%");
        }

        if (!string.IsNullOrWhiteSpace(q.Banco))
            sb.Append(" AND banco = @banco");
        if (!string.IsNullOrWhiteSpace(q.Tabela))
            sb.Append(" AND tabela LIKE @tab");
        if (q.TipoOperacao != null)
            sb.Append(" AND tipo_operacao = @tipo");
        if (q.DataInicio != null)
            sb.Append(" AND data_evento >= @di");
        if (q.DataFim != null)
            sb.Append(" AND data_evento <= @df");
        if (!string.IsNullOrWhiteSpace(q.TextoQuerySql))
            sb.Append(" AND query_sql LIKE @qs");

        return (sb.ToString(), BindWhere);
    }
}
