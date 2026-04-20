using System.Text.RegularExpressions;

namespace MesasLog.Infrastructure.Binlog;

public static class BinlogDirectoryScanner
{
    private static readonly Regex BinlogFileNameRegex = new Regex(
        @"(?i).+(?:-bin|\.bin)\.\d{6,}$",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ListBinlogFiles(string directory)
    {
        if (!Directory.Exists(directory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(directory)
            .Where(f => BinlogFileNameRegex.IsMatch(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
