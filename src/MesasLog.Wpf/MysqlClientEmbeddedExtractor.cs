using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MesasLog.Core;
using Microsoft.Extensions.Logging;

namespace MesasLog.Wpf;

/// <summary>
/// Extrai <c>mysqldump.exe</c> / <c>mysql.exe</c> (e DLLs) de um ZIP embutido como recurso do assembly.
/// Recurso esperado: <c>MesasLog.Wpf.Resources.mysqlclient-bundle.zip</c> (omitido se não existir no build).
/// </summary>
internal static class MysqlClientEmbeddedExtractor
{
    internal const string EmbeddedZipResourceName = "MesasLog.Wpf.Resources.mysqlclient-bundle.zip";

    /// <summary>
    /// Se o ZIP estiver embutido, extrai para dados locais do usuário e preenche <see cref="MysqlCliToolPaths"/>.
    /// Chamada idempotente (reutiliza pasta se o hash coincidir).
    /// </summary>
    internal static void TryEnsureExtracted(ILogger? logger = null)
    {
        try
        {
            var asm = typeof(MysqlClientEmbeddedExtractor).Assembly;
            using var stream = asm.GetManifestResourceStream(EmbeddedZipResourceName);
            if (stream == null)
            {
                logger?.LogDebug("Pacote mysqlclient-bundle.zip não embutido neste build.");
                return;
            }

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var zipBytes = ms.ToArray();
            var hash = HashPrefix(zipBytes);
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MariaDBManager",
                "mysql-tools",
                hash);

            var marker = Path.Combine(root, ".extracted.ok");
            if (File.Exists(marker))
            {
                var dumpCached = FindFile(root, "mysqldump.exe");
                var mysqlCached = FindFile(root, "mysql.exe");
                if (dumpCached != null && mysqlCached != null)
                {
                    ApplyPaths(dumpCached, mysqlCached, root, logger);
                    return;
                }
            }

            if (Directory.Exists(root))
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Não foi possível limpar pasta de extração anterior: {Path}", root);
                }
            }

            Directory.CreateDirectory(root);
            var rootPath = Path.GetFullPath(root);
            var rootFull = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zipStream = new MemoryStream(zipBytes))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                        continue;
                    var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destPath = Path.GetFullPath(Path.Combine(rootPath, rel));
                    if (!destPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogError("Entrada ZIP inválida (path traversal): {Name}", entry.FullName);
                        TryDeleteTree(root);
                        return;
                    }

                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                        Directory.CreateDirectory(destDir);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                        continue;
                    using (var entryStream = entry.Open())
                    using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        entryStream.CopyTo(fs);
                    }
                }
            }

            var dump = FindFile(root, "mysqldump.exe");
            var mysql = FindFile(root, "mysql.exe");
            if (dump == null || mysql == null)
            {
                logger?.LogError(
                    "Pacote embutido mysqlclient-bundle.zip não contém mysqldump.exe e mysql.exe (nem em subpastas).");
                TryDeleteTree(root);
                return;
            }

            File.WriteAllText(marker, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8);
            ApplyPaths(dump, mysql, root, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Falha ao extrair cliente MySQL/MariaDB embutido.");
        }
    }

    private static string HashPrefix(byte[] data)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(data);
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8 && i < h.Length; i++)
            sb.AppendFormat("{0:x2}", h[i]);
        return sb.ToString();
    }

    private static void ApplyPaths(string mysqldump, string mysql, string root, ILogger? logger)
    {
        MysqlCliToolPaths.MysqldumpFullPath = mysqldump;
        MysqlCliToolPaths.MysqlFullPath = mysql;
        MysqlCliToolPaths.BundledExtractRoot = root;
        logger?.LogInformation("Cliente mysql/mysqldump extraído de recurso embutido: {Root}", root);
    }

    private static string? FindFile(string root, string fileName)
    {
        var direct = Path.Combine(root, fileName);
        if (File.Exists(direct))
            return Path.GetFullPath(direct);

        try
        {
            foreach (var path in Directory.GetFiles(root, fileName, SearchOption.AllDirectories))
                return Path.GetFullPath(path);
        }
        catch
        {
            /* ignorar */
        }

        return null;
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }
}
