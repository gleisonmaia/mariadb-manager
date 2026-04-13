namespace MesasLog.Core;

public sealed class EventLogQuery
{
    public string? Banco { get; init; }
    public string? Tabela { get; init; }
    public TipoOperacao? TipoOperacao { get; init; }
    public DateTime? DataInicio { get; init; }
    public DateTime? DataFim { get; init; }
    public string? TextoQuerySql { get; init; }
    public string OrderBy { get; init; } = "data_evento";
    public bool OrderDescending { get; init; } = true;
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
