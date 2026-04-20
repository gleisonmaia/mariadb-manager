using MesasLog.Application.Binlog;
using MesasLog.Application.Validation;
using MesasLog.Core;
using MesasLog.Infrastructure.Binlog;
using MesasLog.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MesasLog.Application.Services;

public sealed class BinlogIngestionService
{
    private readonly IOptions<MesasLogOptions> _options;
    private readonly MysqlBinlogPathResolver _pathResolver;
    private readonly MysqlBinlogExecutor _binlogExecutor;
    private readonly BinlogVerboseRowParser _parser;
    private readonly RowImageValidator _validator;
    private readonly BinlogEventRepository _eventRepository;
    private readonly CheckpointRepository _checkpointRepository;
    private readonly ILogger<BinlogIngestionService> _logger;

    public BinlogIngestionService(
        IOptions<MesasLogOptions> options,
        MysqlBinlogPathResolver pathResolver,
        MysqlBinlogExecutor binlogExecutor,
        BinlogVerboseRowParser parser,
        RowImageValidator validator,
        BinlogEventRepository eventRepository,
        CheckpointRepository checkpointRepository,
        ILogger<BinlogIngestionService> logger)
    {
        _options = options;
        _pathResolver = pathResolver;
        _binlogExecutor = binlogExecutor;
        _parser = parser;
        _validator = validator;
        _eventRepository = eventRepository;
        _checkpointRepository = checkpointRepository;
        _logger = logger;
    }

    public async Task<IngestionResult> RunAsync(bool dryRun, string? targetDatabase, IProgress<string>? progress, CancellationToken ct)
    {
        var opt = _options.Value;
        var messages = new List<string>();
        void Report(string s)
        {
            messages.Add(s);
            progress?.Report(s);
            _logger.LogInformation("{Step}", s);
        }

        var dbName = string.IsNullOrWhiteSpace(targetDatabase)
            ? opt.Processing.TargetDatabase.Trim()
            : targetDatabase.Trim();
        if (string.IsNullOrEmpty(dbName))
            return new IngestionResult(false, "Informe o nome do banco a analisar.", messages);

        Report("Validando conexão com o banco de destino...");
        if (!await _eventRepository.TestConnectionAsync(ct))
            return new IngestionResult(false, "Conexão com o banco falhou.", messages);

        var mysqlBinlog = _pathResolver.ResolveMysqlBinlogExecutable(opt.Binlog.MysqlBinlogPath);
        if (mysqlBinlog == null)
            return new IngestionResult(false, "mysqlbinlog não localizado.", messages);

        var binDir = _pathResolver.ResolveBinlogDirectory(opt.Binlog.BinlogDirectory);
        if (binDir == null)
            return new IngestionResult(false, "Diretório de binlogs não configurado ou não encontrado.", messages);

        var files = BinlogDirectoryScanner.ListBinlogFiles(binDir);
        if (files.Count == 0)
            return new IngestionResult(false, "Nenhum arquivo de binlog encontrado no diretório.", messages);

        var checkpoint = await _checkpointRepository.GetAsync(ct);
        var startIndex = 0;
        long startPosition = 4;
        var checkpointEncontrado = false;
        if (checkpoint != null)
        {
            for (var j = 0; j < files.Count; j++)
            {
                if (!string.Equals(Path.GetFileName(files[j]), checkpoint.File, StringComparison.OrdinalIgnoreCase))
                    continue;
                startIndex = j;
                startPosition = Math.Max(4, checkpoint.Position);
                checkpointEncontrado = true;
                break;
            }

            if (!checkpointEncontrado)
                Report($"Aviso: checkpoint referencia '{checkpoint.File}', que não está no diretório atual de binlogs; reprocessando desde o primeiro ficheiro.");
        }

        Report($"Banco alvo: {dbName} (apenas este schema).");
        Report($"Processando a partir de {Path.GetFileName(files[startIndex])} posição {startPosition}.");

        bool IncludeDb(string d) => string.Equals(d, dbName, StringComparison.OrdinalIgnoreCase);

        var timeout = TimeSpan.FromSeconds(Math.Max(30, opt.Binlog.MysqlBinlogTimeoutSeconds));
        var batchSize = Math.Max(1, opt.Processing.BatchSize);
        var processed = 0;

        for (var i = startIndex; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var fileName = Path.GetFileName(file);
            var pos = i == startIndex ? startPosition : 4;
            var fileLen = new FileInfo(file).Length;
            if (pos >= fileLen)
            {
                Report($"Sem bytes novos em {fileName} (checkpoint em {pos}, tamanho do ficheiro {fileLen} bytes).");
                continue;
            }

            Report($"Executando mysqlbinlog em {fileName} (start-position={pos})...");

            var args = new List<string>
            {
                "--verbose",
                "--base64-output=DECODE-ROWS",
                "--start-position=" + pos.ToString(System.Globalization.CultureInfo.InvariantCulture),
                file
            };

            var exec = await _binlogExecutor.RunAsync(mysqlBinlog, args, timeout, ct);
            if (!exec.Success)
            {
                _logger.LogError("mysqlbinlog stderr: {Err}", exec.StandardError);
                return new IngestionResult(false, "Falha ao executar mysqlbinlog: " + exec.StandardError, messages);
            }

            var lineList = exec.StandardOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var segments = BinlogTransactionSplitter.SplitIntoTransactionSegments(lineList);
            foreach (var segment in segments)
            {
                ct.ThrowIfCancellationRequested();
                var maxAtNoSegmento = BinlogVerboseRowParser.GetMaxAtPosition(segment);
                var events = _parser.ParseLines(segment, fileName, IncludeDb).ToList();

                var validBatch = new List<BinlogParsedRowEvent>();
                foreach (var ev in events)
                {
                    if (!_validator.Validate(ev, out var msg))
                    {
                        if (opt.Processing.OnInconsistency == InconsistencyMode.Abort)
                            return new IngestionResult(false, "Validação: " + msg, messages);
                        continue;
                    }

                    validBatch.Add(ev);
                }

                if (dryRun)
                {
                    if (validBatch.Count > 0)
                        Report($"[dry-run] Transação com {validBatch.Count} evento(s) válido(s).");
                }
                else if (validBatch.Count > 0)
                {
                    for (var o = 0; o < validBatch.Count; o += batchSize)
                    {
                        var chunk = validBatch.Skip(o).Take(batchSize).ToList();
                        await _eventRepository.InsertBatchAsync(chunk, ct);
                        Report($"Persistidos {chunk.Count} evento(s) (mesma transação de binlog em lotes de até {batchSize}).");
                    }
                }

                if (!dryRun && maxAtNoSegmento > 0)
                    await _checkpointRepository.SaveAsync(fileName, maxAtNoSegmento, ct);

                processed += validBatch.Count;
            }
        }

        if (!dryRun && opt.Processing.LogRetentionDays > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-opt.Processing.LogRetentionDays);
            await _eventRepository.DeleteOlderThanAsync(cutoff, ct);
        }

        Report($"Concluído. Eventos processados: {processed}.");
        return new IngestionResult(true, null, messages);
    }
}

public sealed class IngestionResult
{
    public IngestionResult(bool success, string? errorMessage, IReadOnlyList<string> steps)
    {
        Success = success;
        ErrorMessage = errorMessage;
        Steps = steps;
    }

    public bool Success { get; }
    public string? ErrorMessage { get; }
    public IReadOnlyList<string> Steps { get; }
}