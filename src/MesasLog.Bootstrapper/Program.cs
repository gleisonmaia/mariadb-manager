using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace MesasLog.Bootstrapper;

/// <summary>
/// Launcher leve: verifica .NET Framework 4.8 (Release &gt;= 528040) e inicia MariaDBLogExplorer.exe.
/// Na publicação padrão o WPF fica em subpasta "app\".
/// </summary>
internal static class Program
{
    private const string ApplicationSubfolderName = "app";

    /// <summary>Página oficial do instalador offline do .NET Framework 4.8.</summary>
    private const string DefaultFrameworkDownloadUrl =
        "https://dotnet.microsoft.com/pt-br/download/dotnet-framework/net48";

    private const string MainExeName = "MariaDBLogExplorer.exe";
    private const string LauncherConfigFileName = "MariaDBLogExplorer.Launcher.exe.config";
    private const string SettingsFileName = "bootstrapper.settings.json";

    /// <summary>Valor mínimo de Release no registro para considerar .NET 4.8 instalado.</summary>
    private const int MinNet48Release = 528040;

    private static int Main(string[] args)
    {
        ApplyLauncherConfigFromAppSubfolder();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var launcherDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var appDir = ResolveApplicationDirectory(launcherDir);
        var settings = LoadSettings(appDir);
        var exeName = string.IsNullOrWhiteSpace(settings.MainExeName) ? MainExeName : settings.MainExeName!;
        var mainExe = Path.IsPathRooted(exeName)
            ? Path.GetFullPath(exeName)
            : Path.GetFullPath(Path.Combine(appDir, exeName));

        if (!File.Exists(mainExe))
        {
            Console.Error.WriteLine($"Não foi encontrado o aplicativo: {mainExe}");
            Console.Error.WriteLine(
                $"Coloque MariaDBLogExplorer.Launcher.exe na raiz e o conteúdo do app (incluindo MariaDBLogExplorer.exe) na pasta \"{ApplicationSubfolderName}\" (ou tudo na mesma pasta do launcher, layout antigo).");
            PauseIfConsole();
            return 1;
        }

        var downloadUrl = settings.FrameworkDownloadUrl ?? DefaultFrameworkDownloadUrl;

        if (!IsDotNet48OrGreaterInstalled())
        {
            Console.WriteLine(".NET Framework 4.8 não detectado (ou versão abaixo da necessária).");
            Console.WriteLine("É necessário para executar o Maria DB - Log Explorer.");
            Console.WriteLine();
            Console.WriteLine("Abrir a página de download da Microsoft agora? (S/N) [S]: ");
            var line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line) &&
                line.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cancelado.");
                PauseIfConsole();
                return 2;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = downloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Não foi possível abrir o navegador: " + ex.Message);
                Console.Error.WriteLine("Instale o .NET Framework 4.8 manualmente: " + downloadUrl);
                PauseIfConsole();
                return 3;
            }

            Console.WriteLine();
            Console.WriteLine("Após instalar o .NET Framework 4.8, execute novamente o launcher.");
            PauseIfConsole();
            return 0;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                WorkingDirectory = appDir,
                UseShellExecute = true
            });
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Não foi possível iniciar o aplicativo: " + ex.Message);
            PauseIfConsole();
            return 6;
        }
    }

    /// <summary>
    /// Na publicação o <c>.config</c> do launcher fica em <c>app\</c>; o CLR procura por defeito ao lado do .exe.
    /// </summary>
    private static void ApplyLauncherConfigFromAppSubfolder()
    {
        try
        {
            var launcherDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var inApp = Path.Combine(launcherDir, ApplicationSubfolderName, LauncherConfigFileName);
            if (File.Exists(inApp))
                AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", inApp);
        }
        catch
        {
            /* ignorar */
        }
    }

    private static string ResolveApplicationDirectory(string launcherDir)
    {
        var sub = Path.Combine(launcherDir, ApplicationSubfolderName);
        return Directory.Exists(sub) ? sub : launcherDir;
    }

    private static BootstrapperSettings LoadSettings(string baseDir)
    {
        var path = Path.Combine(baseDir, SettingsFileName);
        if (!File.Exists(path)) return new BootstrapperSettings();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BootstrapperSettings>(json, JsonDefaults.JsonReadOptions)
                   ?? new BootstrapperSettings();
        }
        catch
        {
            return new BootstrapperSettings();
        }
    }

    /// <summary>Detecta NDP v4 Full com Release &gt;= 528040 (.NET Framework 4.8).</summary>
    private static bool IsDotNet48OrGreaterInstalled()
    {
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            if (key?.GetValue("Release") is int release)
                return release >= MinNet48Release;
        }

        using (RegistryKey? key32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                   .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            if (key32?.GetValue("Release") is int release32)
                return release32 >= MinNet48Release;
        }

        return false;
    }

    private static void PauseIfConsole()
    {
        try
        {
            if (Environment.UserInteractive)
            {
                Console.WriteLine();
                Console.WriteLine("Pressione Enter para fechar...");
                Console.ReadLine();
            }
        }
        catch
        {
            /* ignorar */
        }
    }
}

internal sealed class BootstrapperSettings
{
    public string? FrameworkDownloadUrl { get; set; }
    public string? MainExeName { get; set; }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions JsonReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
