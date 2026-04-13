using System.Diagnostics;
using System.Text.Json;

namespace MesasLog.Bootstrapper;

/// <summary>
/// Launcher self-contained: verifica Windows Desktop Runtime .NET 8, baixa instalador oficial se necessário
/// e inicia MariaDBLogExplorer.exe (WPF, framework-dependent, single-file). Na publicação padrão o app fica em subpasta "app\".
/// </summary>
internal static class Program
{
    /// <summary>Subpasta com MariaDBLogExplorer.exe (WPF) e bootstrapper.settings.json (publish-release.ps1).</summary>
    private const string ApplicationSubfolderName = "app";

    /// <summary>
    /// Atualize quando publicar (veja https://dotnet.microsoft.com/download/dotnet/8.0 → Desktop Runtime x64).
    /// </summary>
    private const string DefaultRuntimeInstallerUrlX64 =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.14/windowsdesktop-runtime-8.0.14-win-x64.exe";

    private const string MainExeName = "MariaDBLogExplorer.exe";
    private const string SettingsFileName = "bootstrapper.settings.json";
    private const int RequiredDesktopMajor = 8;


    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var appDir = ResolveApplicationDirectory(launcherDir);
        var settings = LoadSettings(appDir);
        var exeName = string.IsNullOrWhiteSpace(settings.MainExeName) ? MainExeName : settings.MainExeName!;
        var mainExe = Path.IsPathRooted(exeName)
            ? Path.GetFullPath(exeName)
            : Path.GetFullPath(Path.Combine(appDir, exeName));

        if (!File.Exists(mainExe))
        {
            Console.Error.WriteLine($"Não foi encontrado o aplicativo: {mainExe}");
            Console.Error.WriteLine($"Coloque MariaDBLogExplorer.Launcher.exe na raiz e o conteúdo do app (incluindo MariaDBLogExplorer.exe) na pasta \"{ApplicationSubfolderName}\" (ou tudo na mesma pasta do launcher, layout antigo).");
            PauseIfConsole();
            return 1;
        }

        var installerUrl = settings.RuntimeInstallerUrl ?? DefaultRuntimeInstallerUrlX64;

        if (!IsWindowsDesktopRuntimeInstalled(settings.RuntimeMajor ?? RequiredDesktopMajor))
        {
            Console.WriteLine(".NET Windows Desktop Runtime não detectado (ou versão principal insuficiente).");
            Console.WriteLine("É necessário para executar o Maria DB - Log Explorer.");
            Console.WriteLine();
            Console.WriteLine("Baixar e instalar agora? (S/N) [S]: ");
            var line = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(line) &&
                line.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Instalação cancelada.");
                PauseIfConsole();
                return 2;
            }

            var tempInstaller = Path.Combine(Path.GetTempPath(), $"windowsdesktop-runtime-setup-{Guid.NewGuid():N}.exe");
            try
            {
                Console.WriteLine($"Baixando instalador da Microsoft...");
                Console.WriteLine(installerUrl);
                await DownloadFileAsync(installerUrl, tempInstaller, Console.WriteLine);

                Console.WriteLine();
                Console.WriteLine("Iniciando instalador (pode solicitar elevação / UAC)...");
                var exit = RunElevatedInstaller(tempInstaller);
                if (exit != 0 && exit != 3010 && exit != 1641)
                {
                    Console.Error.WriteLine($"Instalador retornou código {exit}. Tente instalar manualmente a partir do site da Microsoft.");
                    PauseIfConsole();
                    return 3;
                }

                if (exit is 3010 or 1641)
                    Console.WriteLine("Instalação concluída; pode ser necessário reiniciar o Windows antes de usar o aplicativo.");

                if (!IsWindowsDesktopRuntimeInstalled(settings.RuntimeMajor ?? RequiredDesktopMajor))
                {
                    Console.Error.WriteLine("O runtime ainda não foi detectado. Reinicie o computador ou instale manualmente o Desktop Runtime .NET 8.");
                    PauseIfConsole();
                    return 4;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Falha ao baixar ou instalar o runtime: " + ex.Message);
                PauseIfConsole();
                return 5;
            }
            finally
            {
                TryDelete(tempInstaller);
            }
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

    /// <summary>Pasta com WPF: subpasta "app" se existir; senão a mesma do launcher (compatibilidade).</summary>
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

    /// <summary>Detecta Microsoft.WindowsDesktop.App com versão principal >= major (ex.: 8).</summary>
    private static bool IsWindowsDesktopRuntimeInstalled(int major)
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "shared", "Microsoft.WindowsDesktop.App")
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (Version.TryParse(name, out var v) && v.Major >= major)
                    return true;
            }
        }

        return false;
    }

    private static async Task DownloadFileAsync(string url, string destPath, Action<string>? log)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = true };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MariaDBLogExplorer.Launcher/1.1 (compatible; .NET HTTP client)");
        client.Timeout = TimeSpan.FromMinutes(30);

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n));
            read += n;
            if (total is > 0 and var len && log != null && read % (256 * 1024) < n)
                log($"  {100.0 * read / len:0}% ({read / 1024 / 1024} MB / {len / 1024 / 1024} MB)");
        }
    }

    /// <summary>Executa o instalador com UAC; argumentos padrão da Microsoft para modo silencioso.</summary>
    private static int RunElevatedInstaller(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/install /quiet /norestart",
            UseShellExecute = true,
            Verb = "runas"
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch
        {
            // Usuário negou UAC ou falha ao iniciar
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            /* ignorar */
        }
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
    public string? RuntimeInstallerUrl { get; set; }
    public string? MainExeName { get; set; }
    public int? RuntimeMajor { get; set; }
}

file static class JsonDefaults
{
    public static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
