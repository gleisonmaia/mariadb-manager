using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MesasLog.Application.Services;
using MesasLog.Core;
using MesasLog.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MesasLog.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BinlogIngestionService _ingestion;
    private readonly DataReplayService _replay;
    private readonly BinlogEventRepository _events;
    private readonly SchemaInitializer _schema;
    private readonly MesasLogOptions _opt;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _queryCts;

    public MainViewModel(
        BinlogIngestionService ingestion,
        DataReplayService replay,
        BinlogEventRepository events,
        SchemaInitializer schema,
        IOptions<MesasLogOptions> options,
        ILogger<MainViewModel> logger)
    {
        _ingestion = ingestion;
        _replay = replay;
        _events = events;
        _schema = schema;
        _opt = options.Value;
        _logger = logger;
        NomeBancoProcessamento = string.IsNullOrWhiteSpace(_opt.Processing.TargetDatabase)
            ? "mesas"
            : _opt.Processing.TargetDatabase;
        PageSize = Math.Clamp(_opt.Ui.DefaultPageSize, 1, _opt.Ui.MaxPageSize);
        Ordenacao = "data_evento";
        var hoje = DateTime.Today;
        DataInicio = hoje;
        DataFim = hoje;
    }

    [ObservableProperty] private string _passoAPasso = "";
    [ObservableProperty] private bool _processando;
    [ObservableProperty] private bool _consultando;
    [ObservableProperty] private string _bancoFiltro = "";
    [ObservableProperty] private string _tabelaFiltro = "";
    [ObservableProperty] private string _textoQuerySql = "";
    [ObservableProperty] private DateTime? _dataInicio;
    [ObservableProperty] private DateTime? _dataFim;
    /// <summary>Hora inicial do filtro (formato HH:mm ou HH:mm:ss, 24h).</summary>
    [ObservableProperty] private string _horaInicioTexto = "00:00:00";
    /// <summary>Hora final do filtro (inclusiva; formato HH:mm ou HH:mm:ss).</summary>
    [ObservableProperty] private string _horaFimTexto = "23:59:59";
    [ObservableProperty] private string? _tipoOperacaoSelecionado;
    [ObservableProperty] private string _ordenacao = "data_evento";
    [ObservableProperty] private bool _ordenacaoDescendente = true;
    [ObservableProperty] private int _pageSize = 50;
    [ObservableProperty] private int _paginaAtual = 1;
    [ObservableProperty] private int _totalRegistros;
    /// <summary>Ex.: 1/3 — página atual / total de páginas.</summary>
    [ObservableProperty] private string _textoPaginacao = "1/1";
    [ObservableProperty] private bool _dryRunProcessamento;
    [ObservableProperty] private bool _dryRunReplay;
    /// <summary>Banco cujo binlog ROW será filtrado (correspondência exata).</summary>
    [ObservableProperty] private string _nomeBancoProcessamento = "mesas";

    public ObservableCollection<BinlogEventLogRow> Eventos { get; } = new();

    public string[] TiposOperacaoLista { get; } = ["", "Insert", "Update", "Delete"];

    public string[] OrdenacaoOpcoes { get; } = ["data_evento", "banco", "tabela"];

    [RelayCommand]
    private async Task InicializarEsquemaAsync()
    {
        Processando = true;
        try
        {
            await _schema.EnsureDatabaseExistsAsync();
            await _schema.EnsureCoreSchemaAsync();
            await BundledAppFiles.ApplyMesasSnapshotAsync(_schema);
            AppendPasso("Esquema e snapshot mesas.sql aplicados (arquivo ao lado do .exe ou embutido).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inicializar esquema");
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Processando = false;
        }
    }

    [RelayCommand]
    private async Task ProcessarBinlogAsync()
    {
        Processando = true;
        PassoAPasso = "";
        var progress = new Progress<string>(AppendPasso);
        try
        {
            var result = await _ingestion.RunAsync(DryRunProcessamento, NomeBancoProcessamento, progress, CancellationToken.None);
            if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
                MessageBox.Show(result.ErrorMessage, "Processamento", MessageBoxButton.OK, MessageBoxImage.Warning);
            else if (result.Success)
                MessageBox.Show("Processamento do binlog concluído.", "Processamento", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processar binlog");
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Processando = false;
        }
    }

    [RelayCommand]
    private async Task ReplayAsync()
    {
        Processando = true;
        PassoAPasso = "";
        try
        {
            var r = await _replay.ReplayFromLogAsync(DryRunReplay, CancellationToken.None);
            foreach (var s in r.Steps)
                AppendPasso(s);
            if (!r.Success && r.ErrorMessage != null)
                MessageBox.Show(r.ErrorMessage, "Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay");
            MessageBox.Show(ex.Message, "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Processando = false;
        }
    }

    [RelayCommand]
    private async Task BuscarEventosAsync()
    {
        _queryCts?.Cancel();
        _queryCts?.Dispose();
        _queryCts = new CancellationTokenSource();
        var ct = _queryCts.Token;
        Consultando = true;
        Eventos.Clear();
        try
        {
            TipoOperacao? tipo = string.IsNullOrWhiteSpace(TipoOperacaoSelecionado)
                ? null
                : Enum.Parse<TipoOperacao>(TipoOperacaoSelecionado, true);

            var ps = Math.Clamp(PageSize, 1, _opt.Ui.MaxPageSize);
            PageSize = ps;

            if (!TryMontarIntervaloDatas(out var inicioEfetivo, out var fimEfetivo, out var erroIntervalo))
            {
                MessageBox.Show(erroIntervalo, "Período", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var q = new EventLogQuery
            {
                Banco = string.IsNullOrWhiteSpace(BancoFiltro) ? null : BancoFiltro,
                Tabela = string.IsNullOrWhiteSpace(TabelaFiltro) ? null : TabelaFiltro,
                TextoQuerySql = string.IsNullOrWhiteSpace(TextoQuerySql) ? null : TextoQuerySql,
                DataInicio = inicioEfetivo,
                DataFim = fimEfetivo,
                TipoOperacao = tipo,
                OrderBy = Ordenacao,
                OrderDescending = OrdenacaoDescendente,
                Page = PaginaAtual,
                PageSize = ps
            };

            var page = await _events.QueryAsync(q, ct);
            TotalRegistros = page.TotalCount;
            foreach (var row in page.Items)
                Eventos.Add(row);
            AtualizarTextoPaginacao(ps);
        }
        catch (OperationCanceledException)
        {
            AppendPasso("Consulta cancelada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Buscar eventos");
            MessageBox.Show(ex.Message, "Consulta", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Consultando = false;
        }
    }

    [RelayCommand]
    private void CancelarConsulta()
    {
        _queryCts?.Cancel();
    }

    [RelayCommand]
    private async Task ProximaPaginaAsync()
    {
        PaginaAtual++;
        await BuscarEventosAsync();
    }

    [RelayCommand]
    private async Task PaginaAnteriorAsync()
    {
        if (PaginaAtual > 1)
        {
            PaginaAtual--;
            await BuscarEventosAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(PodeIrPrimeiraPagina))]
    private async Task PrimeiraPaginaAsync()
    {
        if (PaginaAtual <= 1) return;
        PaginaAtual = 1;
        await BuscarEventosAsync();
    }

    private bool PodeIrPrimeiraPagina() => PaginaAtual > 1 && !Consultando;

    [RelayCommand(CanExecute = nameof(PodeIrUltimaPagina))]
    private async Task UltimaPaginaAsync()
    {
        var ultima = CalcularTotalPaginas();
        if (PaginaAtual >= ultima) return;
        PaginaAtual = ultima;
        await BuscarEventosAsync();
    }

    private bool PodeIrUltimaPagina() => !Consultando && PaginaAtual < CalcularTotalPaginas();

    private int CalcularTotalPaginas()
    {
        var ps = Math.Clamp(PageSize, 1, _opt.Ui.MaxPageSize);
        return TotalRegistros <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TotalRegistros / (double)ps));
    }

    partial void OnPaginaAtualChanged(int value) => NotificarComandosPaginacao();

    partial void OnTotalRegistrosChanged(int value) => NotificarComandosPaginacao();

    partial void OnPageSizeChanged(int value) => NotificarComandosPaginacao();

    partial void OnConsultandoChanged(bool value) => NotificarComandosPaginacao();

    private void NotificarComandosPaginacao()
    {
        PrimeiraPaginaCommand.NotifyCanExecuteChanged();
        UltimaPaginaCommand.NotifyCanExecuteChanged();
    }

    private void AppendPasso(string linha)
    {
        PassoAPasso += linha + Environment.NewLine;
    }

    private void AtualizarTextoPaginacao(int pageSizeUsado)
    {
        var ps = Math.Max(1, pageSizeUsado);
        var totalPaginas = TotalRegistros <= 0
            ? 1
            : Math.Max(1, (int)Math.Ceiling(TotalRegistros / (double)ps));
        TextoPaginacao = $"{PaginaAtual}/{totalPaginas}";
    }

    /// <summary>
    /// Combina data (DatePicker) com hora do texto. Vazio em hora: início 00:00:00, fim último instante do dia.
    /// </summary>
    private bool TryMontarIntervaloDatas(out DateTime? inicio, out DateTime? fim, out string erro)
    {
        inicio = null;
        fim = null;
        erro = "";

        if (DataInicio is { } d0)
        {
            if (!TryParseHoraFiltro(HoraInicioTexto, TimeSpan.Zero, out var t0, out var e0))
            {
                erro = e0;
                return false;
            }

            inicio = d0.Date + t0;
        }

        if (DataFim is { } d1)
        {
            var fimDia = TimeSpan.FromDays(1).Subtract(TimeSpan.FromTicks(1));
            if (!TryParseHoraFiltro(HoraFimTexto, fimDia, out var t1, out var e1))
            {
                erro = e1;
                return false;
            }

            fim = d1.Date + t1;
        }

        if (inicio is { } i && fim is { } j && i > j)
        {
            erro = "O início do período não pode ser depois do fim.";
            return false;
        }

        return true;
    }

    private static bool TryParseHoraFiltro(string? texto, TimeSpan padraoSeVazio, out TimeSpan time, out string erro)
    {
        erro = "";
        time = padraoSeVazio;
        if (string.IsNullOrWhiteSpace(texto))
            return true;

        var t = texto.Trim();
        if (TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts))
        {
            if (ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
            {
                erro = $"Hora inválida: \"{t}\". Use valores entre 00:00:00 e 23:59:59.";
                return false;
            }

            time = ts;
            return true;
        }

        var parts = t.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var h)
            && int.TryParse(parts[1], out var m)
            && h is >= 0 and < 24
            && m is >= 0 and < 60)
        {
            time = new TimeSpan(h, m, 0);
            return true;
        }

        erro = $"Hora inválida: \"{t}\". Use HH:mm ou HH:mm:ss (24 horas).";
        return false;
    }
}
