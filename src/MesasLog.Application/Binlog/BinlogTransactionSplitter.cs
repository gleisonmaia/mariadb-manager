namespace MesasLog.Application.Binlog;

public static class BinlogTransactionSplitter
{
    public static IReadOnlyList<IReadOnlyList<string>> SplitIntoTransactionSegments(IEnumerable<string> lines)
    {
        var segments = new List<List<string>>();
        var buf = new List<string>();
        foreach (var line in lines)
        {
            if (IsBeginLine(line) && buf.Count > 0)
            {
                segments.Add(buf);
                buf = new List<string>();
            }

            buf.Add(line);
            if (IsCommitLine(line))
            {
                segments.Add(buf);
                buf = new List<string>();
            }
        }

        if (buf.Count > 0)
            segments.Add(buf);

        return segments.Count > 0 ? segments : (IReadOnlyList<IReadOnlyList<string>>)Array.Empty<IReadOnlyList<string>>();
    }

    private static bool IsBeginLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) && t.Length < 32;
    }

    private static bool IsCommitLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase) && t.Length < 32;
    }
}
