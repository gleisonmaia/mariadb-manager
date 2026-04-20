using System.IO;

namespace MesasLog.Wpf;

/// <summary>
/// Publicação padrão: MariaDBManager.Launcher.exe na raiz e o app em subpasta "app\".
/// Pastas de backup/restauração ficam ao lado do launcher (não dentro de "app\").
/// </summary>
internal static class LauncherLayout
{
    private const string ApplicationSubfolderName = "app";

    /// <summary>Diretório onde está o Launcher (pai de "app" quando o exe roda de "app\").</summary>
    public static string GetLauncherDirectory()
    {
        var baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(Path.GetFileName(baseDir), ApplicationSubfolderName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(baseDir);
            return string.IsNullOrEmpty(parent) ? baseDir : Path.GetFullPath(parent);
        }

        return baseDir;
    }
}
