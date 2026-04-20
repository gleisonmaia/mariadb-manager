using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MesasLog.Infrastructure.Data;

public sealed class BinlogCheckpoint
{
    public BinlogCheckpoint(string file, long position)
    {
        File = file;
        Position = position;
    }

    public string File { get; }
    public long Position { get; }
}

public sealed class CheckpointRepository
{
    private readonly MariaDbConnectionFactory _factory;
    private readonly ILogger<CheckpointRepository> _logger;
    private const int CheckpointId = 1;

    public CheckpointRepository(MariaDbConnectionFactory factory, ILogger<CheckpointRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task<BinlogCheckpoint?> GetAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT binlog_file, binlog_position FROM binlog_checkpoint WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", CheckpointId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new BinlogCheckpoint(r.GetString(0), r.GetInt64(1));
    }

    public async Task SaveAsync(string file, long position, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO binlog_checkpoint (id, binlog_file, binlog_position, updated_at)
            VALUES (@id, @f, @p, @u)
            ON DUPLICATE KEY UPDATE binlog_file = @f, binlog_position = @p, updated_at = @u
            """;
        cmd.Parameters.AddWithValue("@id", CheckpointId);
        cmd.Parameters.AddWithValue("@f", file);
        cmd.Parameters.AddWithValue("@p", position);
        cmd.Parameters.AddWithValue("@u", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogDebug("Checkpoint atualizado: {File} @{Pos}", file, position);
    }
}
