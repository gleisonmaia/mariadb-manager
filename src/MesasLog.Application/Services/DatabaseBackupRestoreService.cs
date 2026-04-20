using System.Diagnostics;
using System.Globalization;
using System.Text;
using MesasLog.Core;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace MesasLog.Application.Services;

/// <summary>
/// Backup e restauração via <c>mysqldump</c> e <c>mysql</c>.
/// Ordem: caminhos após extração de recurso embutido (<see cref="MysqlCliToolPaths"/>); <c>client\</c> ao lado do .exe; PATH.
/// </summary>
public sealed class DatabaseBackupRestoreService
{
    private const string OrigemBackupFolderName = "1-Origem_Backup";
    private const string DestinoRestauracaoFolderName = "2-Destino_Restauracao";

    private const string MysqlDumpExe = "mysqldump.exe";
    private const string MysqlExe = "mysql.exe";

    private readonly ILogger<DatabaseBackupRestoreService> _logger;

    public DatabaseBackupRestoreService(ILogger<DatabaseBackupRestoreService> logger)
    {
        _logger = logger;
    }

    /// <summary>Slug para nome de arquivo: mysql ou mariadb.</summary>
    public static string SlugTipoBanco(string? tipoBanco) =>
        tipoBanco != null && string.Equals(tipoBanco.Trim(), "MariaDB", StringComparison.OrdinalIgnoreCase)
            ? "mariadb"
            : "mysql";

    public static string FormatarTimestampArquivo(DateTime dt) =>
        dt.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

    /// <summary>Caminho completo do .sql gerado.</summary>
    public async Task<string> RealizarBackupOrigemAsync(
        string launcherDirectory,
        string tipoBanco,
        string host,
        int port,
        string database,
        string user,
        string password,
        IProgress<string> progress,
        CancellationToken ct)
    {
        ValidarEndpoint(host, port, database, user);
        var pasta = Path.Combine(launcherDirectory, OrigemBackupFolderName);
        Directory.CreateDirectory(pasta);
        var slug = SlugTipoBanco(tipoBanco);
        var dbFile = SanitizeSegmentoArquivo(database);
        var stamp = FormatarTimestampArquivo(DateTime.Now);
        var nomeArquivo = $"backup_origem_{slug}_{dbFile}_{stamp}.sql";
        var caminhoCompleto = Path.Combine(pasta, nomeArquivo);

        progress.Report($"Pasta de destino: {pasta}");
        progress.Report($"Arquivo: {nomeArquivo}");
        progress.Report("Executando mysqldump (estrutura e dados)...");

        await ExecutarMysqldumpAsync(host, port, user, password, database, caminhoCompleto, progress, ct)
            .ConfigureAwait(false);

        progress.Report($"Backup concluído: {caminhoCompleto}");
        return caminhoCompleto;
    }

    /// <summary>Se o schema existir no destino, gera backup em 2-Destino_Restauracao; em seguida importa o .sql.</summary>
    public async Task RestaurarDestinoAsync(
        string launcherDirectory,
        string tipoBanco,
        string host,
        int port,
        string database,
        string user,
        string password,
        string arquivoSql,
        IProgress<string> progress,
        CancellationToken ct)
    {
        ValidarEndpoint(host, port, database, user);
        if (string.IsNullOrWhiteSpace(arquivoSql) || !File.Exists(arquivoSql))
            throw new FileNotFoundException("Arquivo SQL não encontrado.", arquivoSql);

        if (!string.Equals(Path.GetExtension(arquivoSql), ".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selecione um arquivo com extensão .sql.");

        progress.Report($"Arquivo de importação: {arquivoSql}");

        var existe = await SchemaExisteAsync(host, port, user, password, database, ct).ConfigureAwait(false);
        if (existe)
        {
            progress.Report($"O banco \"{database}\" já existe no destino. Será feito backup de segurança antes da restauração.");
            var pastaDest = Path.Combine(launcherDirectory, DestinoRestauracaoFolderName);
            Directory.CreateDirectory(pastaDest);
            var slug = SlugTipoBanco(tipoBanco);
            var dbFile = SanitizeSegmentoArquivo(database);
            var stamp = FormatarTimestampArquivo(DateTime.Now);
            var nomeArquivo = $"backup_destino_{slug}_{dbFile}_{stamp}.sql";
            var caminhoBackupDestino = Path.Combine(pastaDest, nomeArquivo);
            progress.Report($"Salvando backup do destino em: {caminhoBackupDestino}");
            await ExecutarMysqldumpAsync(host, port, user, password, database, caminhoBackupDestino, progress, ct)
                .ConfigureAwait(false);
            progress.Report("Backup do destino concluído.");
        }
        else
            progress.Report($"O banco \"{database}\" não existe no destino; será criado e importado.");

        progress.Report("Garantindo existência do banco de dados...");
        await GarantirDatabaseAsync(host, port, user, password, database, ct).ConfigureAwait(false);

        progress.Report("Importando arquivo SQL (mysql)...");
        await ExecutarMysqlImportAsync(host, port, user, password, database, arquivoSql, progress, ct).ConfigureAwait(false);
        progress.Report("Restauração concluída.");
    }

    private async Task<bool> SchemaExisteAsync(string host, int port, string user, string password, string database, CancellationToken ct)
    {
        var csb = CriarBuilderServidor(host, port, user, password);
        await using var conn = new MySqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @n";
        cmd.Parameters.AddWithValue("@n", database);
        var o = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(o, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task GarantirDatabaseAsync(string host, int port, string user, string password, string database, CancellationToken ct)
    {
        var id = EscaparIdentificadorBacktick(database);
        var csb = CriarBuilderServidor(host, port, user, password);
        await using var conn = new MySqlConnection(csb.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{id}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string EscaparIdentificadorBacktick(string name) => name.Replace("`", "``");

    private static MySqlConnectionStringBuilder CriarBuilderServidor(string host, int port, string user, string password) =>
        new()
        {
            Server = host,
            Port = (uint)port,
            UserID = user,
            Password = password,
            CharacterSet = "utf8mb4"
        };

    private static void ValidarEndpoint(string host, int port, string database, string user)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("Informe o servidor.");
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Porta inválida.");
        if (string.IsNullOrWhiteSpace(database))
            throw new InvalidOperationException("Informe o nome do banco.");
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("Informe o usuário.");
    }

    private static string SanitizeSegmentoArquivo(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "banco";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "banco" : s;
    }

    private static (string FileName, string? WorkingDirectory) ResolverMysqldump(IProgress<string> progress)
    {
        if (TryResolveBundled(MysqlDumpExe, out var path, out var wd))
        {
            progress.Report("Usando mysqldump: " + path);
            return (path, wd);
        }

        progress.Report("Usando mysqldump do PATH do sistema (" + MysqlDumpExe + ").");
        return (MysqlDumpExe, null);
    }

    private static (string FileName, string? WorkingDirectory) ResolverMysql(IProgress<string> progress)
    {
        if (TryResolveBundled(MysqlExe, out var path, out var wd))
        {
            progress.Report("Usando mysql: " + path);
            return (path, wd);
        }

        progress.Report("Usando mysql do PATH do sistema (" + MysqlExe + ").");
        return (MysqlExe, null);
    }

    /// <summary>Extrato embutido, depois <c>client\</c> / <c>client\bin\</c> ao lado do executável.</summary>
    private static bool TryResolveBundled(string exeName, out string fullPath, out string? workingDirectory)
    {
        if (exeName.Equals(MysqlDumpExe, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(MysqlCliToolPaths.MysqldumpFullPath)
            && File.Exists(MysqlCliToolPaths.MysqldumpFullPath))
        {
            fullPath = Path.GetFullPath(MysqlCliToolPaths.MysqldumpFullPath);
            workingDirectory = Path.GetDirectoryName(fullPath);
            return true;
        }

        if (exeName.Equals(MysqlExe, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(MysqlCliToolPaths.MysqlFullPath)
            && File.Exists(MysqlCliToolPaths.MysqlFullPath))
        {
            fullPath = Path.GetFullPath(MysqlCliToolPaths.MysqlFullPath);
            workingDirectory = Path.GetDirectoryName(fullPath);
            return true;
        }

        var baseDir = Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var rel in new[] { Path.Combine("client", exeName), Path.Combine("client", "bin", exeName) })
        {
            var p = Path.Combine(baseDir, rel);
            if (File.Exists(p))
            {
                fullPath = Path.GetFullPath(p);
                workingDirectory = Path.GetDirectoryName(fullPath);
                return true;
            }
        }

        fullPath = "";
        workingDirectory = null;
        return false;
    }

    private async Task ExecutarMysqldumpAsync(
        string host,
        int port,
        string user,
        string password,
        string database,
        string arquivoSaida,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var passwordFile = Path.GetTempFileName();
        try
        {
            EscreverArquivoSenhaCliente(passwordFile, password);
            var args = new StringBuilder();
            args.Append("--defaults-extra-file=\"").Append(passwordFile).Append("\" ");
            args.Append("--protocol=tcp ");
            args.Append("-h ").Append(QuoteArg(host)).Append(' ');
            args.Append("-P ").Append(port).Append(' ');
            args.Append("-u ").Append(QuoteArg(user)).Append(' ');
            args.Append("--single-transaction --routines --triggers ");
            args.Append("--result-file=\"").Append(arquivoSaida).Append("\" ");
            args.Append(QuoteArg(database));

            var tool = ResolverMysqldump(progress);
            await ExecutarProcessoAsync(tool, args.ToString(), progress, ct, redirecionarStdout: false).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(passwordFile);
        }
    }

    private async Task ExecutarMysqlImportAsync(
        string host,
        int port,
        string user,
        string password,
        string database,
        string arquivoSql,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var passwordFile = Path.GetTempFileName();
        try
        {
            EscreverArquivoSenhaCliente(passwordFile, password);
            var args = new StringBuilder();
            args.Append("--defaults-extra-file=\"").Append(passwordFile).Append("\" ");
            args.Append("--protocol=tcp ");
            args.Append("-h ").Append(QuoteArg(host)).Append(' ');
            args.Append("-P ").Append(port).Append(' ');
            args.Append("-u ").Append(QuoteArg(user)).Append(' ');
            args.Append(QuoteArg(database));

            var tool = ResolverMysql(progress);
            await ExecutarProcessoComStdinDeArquivoAsync(tool, args.ToString(), arquivoSql, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(passwordFile);
        }
    }

    private static void EscreverArquivoSenhaCliente(string path, string password)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[client]");
        var pwd = password.Replace("\r", "").Replace("\n", "");
        sb.Append("password=");
        if (pwd.IndexOfAny(new[] { '"', '\'', '\\' }) >= 0)
            sb.Append('"').Append(pwd.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
        else
            sb.Append(pwd);
        sb.AppendLine();
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string QuoteArg(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";
        if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private async Task ExecutarProcessoAsync(
        (string FileName, string? WorkingDirectory) tool,
        string arguments,
        IProgress<string> progress,
        CancellationToken ct,
        bool redirecionarStdout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool.FileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = redirecionarStdout,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (tool.WorkingDirectory != null)
            psi.WorkingDirectory = tool.WorkingDirectory;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Não foi possível iniciar {tool.FileName}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar processo {Exe}", tool.FileName);
            throw new InvalidOperationException(MensagemClienteNaoEncontrado(tool.FileName), ex);
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        await Task.Run(() =>
        {
            process.WaitForExit();
        }, ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            var msg = string.IsNullOrEmpty(err)
                ? $"{tool.FileName} terminou com código {process.ExitCode}."
                : err;
            throw new InvalidOperationException(msg);
        }

        var warn = stderr.ToString().Trim();
        if (!string.IsNullOrEmpty(warn))
            progress.Report("Avisos: " + warn);
    }

    private async Task ExecutarProcessoComStdinDeArquivoAsync(
        (string FileName, string? WorkingDirectory) tool,
        string arguments,
        string arquivoEntrada,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool.FileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (tool.WorkingDirectory != null)
            psi.WorkingDirectory = tool.WorkingDirectory;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Não foi possível iniciar {tool.FileName}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao iniciar processo {Exe}", tool.FileName);
            throw new InvalidOperationException(MensagemClienteNaoEncontrado(tool.FileName), ex);
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        await Task.Run(() =>
        {
            using (var fs = new FileStream(arquivoEntrada, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            using (var stdin = process.StandardInput.BaseStream)
            {
                fs.CopyTo(stdin);
            }

            process.StandardInput.Close();
            process.WaitForExit();
        }, ct).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            var msg = string.IsNullOrEmpty(err)
                ? $"{tool.FileName} terminou com código {process.ExitCode}."
                : err;
            throw new InvalidOperationException(msg);
        }

        var warn = stderr.ToString().Trim();
        if (!string.IsNullOrEmpty(warn))
            progress.Report("Avisos: " + warn);
    }

    private static string MensagemClienteNaoEncontrado(string tentativa) =>
        $"Não foi possível executar \"{tentativa}\". Use uma build que inclua o recurso embutido mysqlclient-bundle.zip, " +
        $"ou copie para a subpasta \"client\" os executáveis {MysqlDumpExe} e {MysqlExe} (e DLLs), " +
        "ou instale o cliente MariaDB/MySQL e garanta esses nomes no PATH do Windows.";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }
}
