using System.Collections.ObjectModel;
using System.Diagnostics;
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
using Microsoft.Win32;

namespace MesasLog.Wpf.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BinlogIngestionService _ingestion;
    private readonly DataReplayService _replay;
    private readonly BinlogEventRepository _events;
    private readonly SchemaInitializer _schema;
    private readonly DatabaseBackupRestoreService _backupRestore;
    private readonly MesasLogOptions _opt;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _queryCts;

    public MainViewModel(
        BinlogIngestionService ingestion,
        DataReplayService replay,
        BinlogEventRepository events,
        SchemaInitializer schema,
        DatabaseBackupRestoreService backupRestore,
        IOptions<MesasLogOptions> options,
        ILogger<MainViewModel> logger)
    {
        _ingestion = ingestion;
        _replay = replay;
        _events = events;
        _schema = schema;
        _backupRestore = backupRestore;
        _opt = options.Value;
        _logger = logger;
        NomeBancoProcessamento = string.IsNullOrWhiteSpace(_opt.Processing.TargetDatabase)
            ? "mesas"
            : _opt.Processing.TargetDatabase;
        PageSize = ClampInt(_opt.Ui.DefaultPageSize, 1, _opt.Ui.MaxPageSize);
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
    /// <summary>Aba Avançado utilizável; inicia bloqueada — Ctrl+Shift+F11 alterna.</summary>
    [ObservableProperty] private bool _abaAvancadaDesbloqueada;

    [ObservableProperty] private string? _tipoBancoOrigem;
    [ObservableProperty] private string _servidorOrigem = "";
    [ObservableProperty] private string _portaOrigemTexto = "3306";
    [ObservableProperty] private string _bancoOrigem = "mesas";
    [ObservableProperty] private string _usuarioOrigem = "softcom";
    [ObservableProperty] private string _senhaOrigem = "";
    [ObservableProperty] private string _passoPassoBackupOrigem = "";
    [ObservableProperty] private bool _processandoBackupOrigem;

    [ObservableProperty] private string? _tipoBancoDestino;
    [ObservableProperty] private string _servidorDestino = "";
    [ObservableProperty] private string _portaDestinoTexto = "3306";
    [ObservableProperty] private string _bancoDestino = "mesas";
    [ObservableProperty] private string _usuarioDestino = "softcom";
    [ObservableProperty] private string _senhaDestino = "";
    [ObservableProperty] private string _arquivoSqlDestino = "";
    [ObservableProperty] private string _passoPassoRestauracao = "";
    [ObservableProperty] private bool _processandoRestauracao;

    public ObservableCollection<BinlogEventLogRow> Eventos { get; } = new();

    public string[] TiposOperacaoLista { get; } = { "", "Insert", "Update", "Delete" };

    public string[] OrdenacaoOpcoes { get; } = { "data_evento", "banco", "tabela" };

    public string[] TiposBancoBackup { get; } = { "MySQL", "MariaDB" };

    [RelayCommand]
    private async Task RealizarBackupOrigemAsync()
    {
        if (!TryValidarEndpointOrigem(out var host, out var port, out var erro))
        {
            MessageBox.Show(erro, "Backup (origem)", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProcessandoBackupOrigem = true;
        PassoPassoBackupOrigem = "";
        var inicio = DateTime.Now;
        AppendPassoBackup($"Início: {FormatarDataHoraLocal(inicio)}");
        var progress = new Progress<string>(AppendPassoBackup);
        try
        {
            var launcherDir = LauncherLayout.GetLauncherDirectory();
            AppendPassoBackup($"Pasta do launcher: {launcherDir}");
            var caminho = await _backupRestore.RealizarBackupOrigemAsync(
                launcherDir,
                TipoBancoOrigem!,
                host,
                port,
                BancoOrigem.Trim(),
                UsuarioOrigem.Trim(),
                SenhaOrigem,
                progress,
                CancellationToken.None);

            var pasta = Path.GetDirectoryName(caminho) ?? launcherDir;
            MessageBox.Show(
                "Backup realizado com sucesso em " + caminho + ". Confira se os dados foram gerados corretamente.",
                "Backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            if (MessageBox.Show(
                    "Deseja abrir a pasta do backup?",
                    "Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = pasta,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Abrir pasta do backup");
                    MessageBox.Show("Não foi possível abrir a pasta: " + ex.Message, "Explorador", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup origem");
            AppendPassoBackup("Erro: " + ex.Message);
            MessageBox.Show(ex.Message, "Backup (origem)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AppendPassoBackup($"Término: {FormatarDataHoraLocal(DateTime.Now)}");
            ProcessandoBackupOrigem = false;
        }
    }

    [RelayCommand]
    private void SelecionarArquivoSqlDestino()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Arquivos SQL (*.sql)|*.sql|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
            ArquivoSqlDestino = dlg.FileName;
    }

    [RelayCommand]
    private async Task RestaurarBancoDestinoAsync()
    {
        if (!TryValidarEndpointDestino(out var host, out var port, out var erro))
        {
            MessageBox.Show(erro, "Restauração (destino)", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(ArquivoSqlDestino) || !File.Exists(ArquivoSqlDestino))
        {
            MessageBox.Show("Selecione um arquivo .sql existente.", "Restauração (destino)", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(Path.GetExtension(ArquivoSqlDestino), ".sql", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Apenas arquivos com extensão .sql são permitidos.", "Restauração (destino)", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProcessandoRestauracao = true;
        PassoPassoRestauracao = "";
        var inicio = DateTime.Now;
        AppendPassoRestauracao($"Início: {FormatarDataHoraLocal(inicio)}");
        var progress = new Progress<string>(AppendPassoRestauracao);
        try
        {
            var launcherDir = LauncherLayout.GetLauncherDirectory();
            AppendPassoRestauracao($"Pasta do launcher: {launcherDir}");
            await _backupRestore.RestaurarDestinoAsync(
                launcherDir,
                TipoBancoDestino!,
                host,
                port,
                BancoDestino.Trim(),
                UsuarioDestino.Trim(),
                SenhaDestino,
                ArquivoSqlDestino,
                progress,
                CancellationToken.None);

            MessageBox.Show("Restauração do banco concluída.", "Restauração", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restauração destino");
            AppendPassoRestauracao("Erro: " + ex.Message);
            MessageBox.Show(ex.Message, "Restauração (destino)", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AppendPassoRestauracao($"Término: {FormatarDataHoraLocal(DateTime.Now)}");
            ProcessandoRestauracao = false;
        }
    }

    private bool TryValidarEndpointOrigem(out string host, out int port, out string erro)
    {
        host = ServidorOrigem.Trim();
        port = 3306;
        erro = "";
        if (string.IsNullOrWhiteSpace(TipoBancoOrigem))
        {
            erro = "Selecione o tipo de banco (MySQL ou MariaDB) na origem.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            erro = "Informe o servidor (origem).";
            return false;
        }

        if (!int.TryParse(PortaOrigemTexto?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            erro = "Informe uma porta válida (origem).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(BancoOrigem))
        {
            erro = "Informe o nome do banco (origem).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UsuarioOrigem))
        {
            erro = "Informe o usuário (origem).";
            return false;
        }

        return true;
    }

    private bool TryValidarEndpointDestino(out string host, out int port, out string erro)
    {
        host = ServidorDestino.Trim();
        port = 3306;
        erro = "";
        if (string.IsNullOrWhiteSpace(TipoBancoDestino))
        {
            erro = "Selecione o tipo de banco (MySQL ou MariaDB) no destino.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            erro = "Informe o servidor (destino).";
            return false;
        }

        if (!int.TryParse(PortaDestinoTexto?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            erro = "Informe uma porta válida (destino).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(BancoDestino))
        {
            erro = "Informe o nome do banco (destino).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(UsuarioDestino))
        {
            erro = "Informe o usuário (destino).";
            return false;
        }

        return true;
    }

    private void AppendPassoBackup(string linha) =>
        PassoPassoBackupOrigem += linha + Environment.NewLine;

    private void AppendPassoRestauracao(string linha) =>
        PassoPassoRestauracao += linha + Environment.NewLine;

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
        var inicio = DateTime.Now;
        Processando = true;
        PassoAPasso = "";
        AppendPasso($"Início: {FormatarDataHoraLocal(inicio)}");
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
            var fim = DateTime.Now;
            AppendPasso($"Término: {FormatarDataHoraLocal(fim)}");
            AppendPasso($"Duração: {FormatarDuracao(fim - inicio)}");
            Processando = false;
        }
    }

    [RelayCommand]
    private void AlternarAbaAvancada() => AbaAvancadaDesbloqueada = !AbaAvancadaDesbloqueada;

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
                : (TipoOperacao)Enum.Parse(typeof(TipoOperacao), TipoOperacaoSelecionado, true);

            var ps = ClampInt(PageSize, 1, _opt.Ui.MaxPageSize);
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
        var ps = ClampInt(PageSize, 1, _opt.Ui.MaxPageSize);
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

    private static string FormatarDataHoraLocal(DateTime dt) =>
        dt.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR"));

    private static int ClampInt(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string FormatarDuracao(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        var totalSeconds = (long)d.TotalSeconds;
        var h = (int)(totalSeconds / 3600);
        var m = (int)(totalSeconds % 3600 / 60);
        var s = (int)(totalSeconds % 60);
        return $"{h:D2}:{m:D2}:{s:D2}";
    }
}
