namespace MesasLog.Core;

public sealed class BinlogEventLogRow
{
    public long Id { get; init; }
    public DateTime DataEvento { get; init; }
    public string Banco { get; init; } = "";
    public string Tabela { get; init; } = "";
    public TipoOperacao TipoOperacao { get; init; }
    public string? DadosAntesJson { get; init; }
    public string? DadosDepoisJson { get; init; }
    public string? QuerySql { get; init; }
    public string BinlogFile { get; init; } = "";
    public long BinlogPosition { get; init; }
}
