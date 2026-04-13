namespace MesasLog.Core;

public sealed class BinlogParsedRowEvent
{
    public required string BinlogFile { get; init; }
    public long BinlogPosition { get; init; }
    public DateTime? EventTimestamp { get; init; }
    public required string Database { get; init; }
    public required string Table { get; init; }
    public TipoOperacao Operation { get; init; }
    public JsonRowPayload? Before { get; init; }
    public JsonRowPayload? After { get; init; }
    public string? QuerySql { get; init; }
}
