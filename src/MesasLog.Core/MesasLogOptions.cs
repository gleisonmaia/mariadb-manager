namespace MesasLog.Core;

public sealed class MesasLogOptions
{
    public const string SectionName = "MesasLog";

    public DatabaseSettings Database { get; set; } = new();
    public BinlogSettings Binlog { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

public sealed class DatabaseSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "mesas_log";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class BinlogSettings
{
    /// <summary>Vazio: tentar detectar pasta de binlogs do MariaDB.</summary>
    public string? BinlogDirectory { get; set; }

    /// <summary>Caminho completo do executável mysqlbinlog; vazio: auto.</summary>
    public string? MysqlBinlogPath { get; set; }

    public int MysqlBinlogTimeoutSeconds { get; set; } = 300;
}

public sealed class ProcessingSettings
{
    public int BatchSize { get; set; } = 500;
    public int LogRetentionDays { get; set; } = 90;
    /// <summary>Nome do banco cujos eventos ROW serão ingeridos (correspondência exata, ignorando maiúsculas/minúsculas).</summary>
    public string TargetDatabase { get; set; } = "mesas";
    public InsertConflictBehavior InsertConflict { get; set; } = InsertConflictBehavior.Ignore;
    public InconsistencyMode OnInconsistency { get; set; } = InconsistencyMode.Abort;
}

public sealed class LoggingSettings
{
    public string LogDirectory { get; set; } = "logs";
    public string MinimumLevel { get; set; } = "Information";
}

public sealed class UiSettings
{
    public int MaxPageSize { get; set; } = 500;
    public int DefaultPageSize { get; set; } = 50;
}
