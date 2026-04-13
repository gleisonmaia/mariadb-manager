using Microsoft.Extensions.Logging;

namespace MesasLog.Infrastructure.Binlog;

public sealed class MysqlBinlogPathResolver(ILogger<MysqlBinlogPathResolver> logger)
{
    public string? ResolveMysqlBinlogExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        var fromPath = FindOnPath("mysqlbinlog.exe");
        if (fromPath != null) return fromPath;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrEmpty(x)).Distinct();

        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "MariaDB *", SearchOption.TopDirectoryOnly))
                {
                    var bin = Path.Combine(dir, "bin", "mysqlbinlog.exe");
                    if (File.Exists(bin)) return bin;
                }

                foreach (var dir in Directory.EnumerateDirectories(root, "MySQL *", SearchOption.TopDirectoryOnly))
                {
                    var bin = Path.Combine(dir, "bin", "mysqlbinlog.exe");
                    if (File.Exists(bin)) return bin;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Busca em {Root} ignorada.", root);
            }
        }

        logger.LogWarning("mysqlbinlog não encontrado. Configure MesasLog:Binlog:MysqlBinlogPath ou instale o MariaDB/MySQL Client.");
        return null;
    }

    public string? ResolveBinlogDirectory(string? configuredDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
            return configuredDir;

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(x => !string.IsNullOrEmpty(x)).Distinct();

        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "MariaDB *", SearchOption.TopDirectoryOnly))
                {
                    var data = Path.Combine(dir, "data");
                    if (Directory.Exists(data) && HasBinlogFiles(data)) return data;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Busca de data dir em {Root} ignorada.", root);
            }
        }

        logger.LogWarning("Diretório de binlogs não detectado. Configure MesasLog:Binlog:BinlogDirectory.");
        return null;
    }

    private static bool HasBinlogFiles(string dataDir)
    {
        try
        {
            return Directory.EnumerateFiles(dataDir, "*bin.*", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(segment.Trim('"'), fileName);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
