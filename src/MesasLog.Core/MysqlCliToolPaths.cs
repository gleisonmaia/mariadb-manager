namespace MesasLog.Core;

/// <summary>
/// Caminhos dos executáveis mysql/mysqldump após extração de recurso embutido (definidos pelo host WPF).
/// </summary>
public static class MysqlCliToolPaths
{
    /// <summary>Caminho absoluto para mysqldump.exe quando extraído do pacote embutido.</summary>
    public static string? MysqldumpFullPath { get; set; }

    /// <summary>Caminho absoluto para mysql.exe quando extraído do pacote embutido.</summary>
    public static string? MysqlFullPath { get; set; }

    /// <summary>Pasta raiz da extração (opcional, para diagnóstico).</summary>
    public static string? BundledExtractRoot { get; set; }
}
