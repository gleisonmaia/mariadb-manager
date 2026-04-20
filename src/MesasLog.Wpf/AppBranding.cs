using System.Reflection;

namespace MesasLog.Wpf;

/// <summary>Textos de identificação da UI (título da janela, etc.).</summary>
internal static class AppBranding
{
    /// <summary>Título base da janela principal (sem versão).</summary>
    public const string MainWindowTitle = "MariaDB Manager";

    public static string GetMainWindowTitleWithVersion(Assembly assembly)
    {
        var v = GetDisplayVersion(assembly);
        return string.IsNullOrEmpty(v) ? MainWindowTitle : $"{MainWindowTitle} {v}";
    }

    public static string GetDisplayVersion(Assembly asm)
    {
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info.Substring(0, plus) : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
