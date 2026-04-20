using MesasLog.Core;
using Microsoft.Extensions.Logging;

namespace MesasLog.Application.Validation;

public sealed class RowImageValidator
{
    private readonly ILogger<RowImageValidator> _logger;

    public RowImageValidator(ILogger<RowImageValidator> logger)
    {
        _logger = logger;
    }

    public bool Validate(BinlogParsedRowEvent e, out string? message)
    {
        message = null;

        switch (e.Operation)
        {
            case TipoOperacao.Insert:
                if (e.Before is { Values.Count: > 0 })
                {
                    message = "INSERT com dados_antes inesperado.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                if (e.After is not { Values.Count: > 0 })
                {
                    message = "INSERT sem dados_depois.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                break;
            case TipoOperacao.Delete:
                if (e.After is { Values.Count: > 0 })
                {
                    message = "DELETE com dados_depois inesperado.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                if (e.Before is not { Values.Count: > 0 })
                {
                    message = "DELETE sem dados_antes.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                break;
            case TipoOperacao.Update:
                if (e.Before is not { Values.Count: > 0 } || e.After is not { Values.Count: > 0 })
                {
                    message = "UPDATE exige dados_antes e dados_depois.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                if (!HasAnyDifference(e.Before, e.After))
                {
                    message = "UPDATE sem colunas alteradas entre antes e depois.";
                    _logger.LogError("{Msg}", message);
                    return false;
                }

                break;
        }

        return true;
    }

    private static bool HasAnyDifference(JsonRowPayload before, JsonRowPayload after)
    {
        foreach (var key in after.Columns)
        {
            after.Values.TryGetValue(key, out var av);
            before.Values.TryGetValue(key, out var bv);
            if (!Equals(av, bv)) return true;
        }

        return false;
    }
}
