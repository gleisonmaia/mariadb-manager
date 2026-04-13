using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MesasLog.Core;
using MesasLog.Wpf.ViewModels;

namespace MesasLog.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplyWindowTitle();
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowTitle();
    }

    private void ApplyWindowTitle()
    {
        Title = AppBranding.GetMainWindowTitleWithVersion(typeof(MainWindow).Assembly);
    }

    private void PassoAPassoTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.CaretIndex = tb.Text.Length;
        tb.ScrollToEnd();
    }

    private void EventosDataGrid_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid) return;

        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.DataContext is not BinlogEventLogRow row || cell.Column is not DataGridTextColumn textColumn)
            return;

        if (GetCellText(row, textColumn) is not { } text)
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Área de transferência indisponível (ex.: acesso remoto).
        }
    }

    private static string? GetCellText(BinlogEventLogRow row, DataGridTextColumn column)
    {
        if (column.Binding is not Binding binding || binding.Path.Path is not { } path)
            return null;

        return path switch
        {
            nameof(BinlogEventLogRow.DataEvento) => row.DataEvento.ToString("yyyy-MM-dd HH:mm:ss"),
            nameof(BinlogEventLogRow.Banco) => row.Banco,
            nameof(BinlogEventLogRow.Tabela) => row.Tabela,
            nameof(BinlogEventLogRow.TipoOperacao) => row.TipoOperacao.ToString(),
            nameof(BinlogEventLogRow.QuerySql) => row.QuerySql ?? string.Empty,
            _ => null
        };
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T match)
                return match;
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }
}
