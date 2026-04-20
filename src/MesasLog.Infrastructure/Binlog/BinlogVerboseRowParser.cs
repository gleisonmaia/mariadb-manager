using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MesasLog.Core;

namespace MesasLog.Infrastructure.Binlog;

/// <summary>
/// Interpreta saída textual de mysqlbinlog --verbose --base64-output=DECODE-ROWS (eventos ROW).
/// </summary>
public sealed class BinlogVerboseRowParser
{
    private static readonly Regex AtPositionRegex = new Regex(
        @"^#\s*at\s+(\d+)\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex EventTimestampRegex = new Regex(
        @"^#(\d{6})\s+(\d{1,2}:\d{2}:\d{2})",
        RegexOptions.CultureInvariant);

    private static readonly Regex RowOperationRegex = new Regex(
        @"^###\s+(INSERT INTO|UPDATE|DELETE FROM)\s+`([^`]+)`\.`([^`]+)`\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex RowColumnRegex = new Regex(
        @"^###\s+@(\d+)=(.+)$",
        RegexOptions.CultureInvariant);

    /// <summary>Maior posição (# at N) presente nas linhas; 0 se não houver.</summary>
    public static long GetMaxAtPosition(IEnumerable<string> lines)
    {
        long max = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var at = AtPositionRegex.Match(line);
            if (!at.Success) continue;
            var p = long.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
            if (p > max) max = p;
        }

        return max;
    }

    public IEnumerable<BinlogParsedRowEvent> ParseLines(
        IEnumerable<string> lines,
        string binlogFile,
        Func<string, bool> includeDatabase)
    {
        long position = 0;
        DateTime? eventTime = null;

        Pending? pending = null;
        Phase phase = Phase.Idle;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');

            var at = AtPositionRegex.Match(line);
            if (at.Success)
            {
                position = long.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var ts = EventTimestampRegex.Match(line);
            if (ts.Success && TryParseBinlogTimestamp(ts.Groups[1].Value, ts.Groups[2].Value, out var dt))
            {
                eventTime = dt;
                continue;
            }

            var opm = RowOperationRegex.Match(line);
            if (opm.Success)
            {
                var flushed = TakePending(ref pending);
                if (flushed != null) yield return flushed;

                var db = opm.Groups[2].Value;
                var tbl = opm.Groups[3].Value;
                if (!includeDatabase(db))
                {
                    pending = null;
                    phase = Phase.Idle;
                    continue;
                }

                var op = opm.Groups[1].Value switch
                {
                    var s when s.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) => TipoOperacao.Insert,
                    var s when s.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) => TipoOperacao.Update,
                    _ => TipoOperacao.Delete
                };

                pending = new Pending
                {
                    BinlogFile = binlogFile,
                    Position = position,
                    EventTime = eventTime,
                    Database = db,
                    Table = tbl,
                    Operation = op,
                    Before = op is TipoOperacao.Update or TipoOperacao.Delete ? new Dictionary<string, object?>() : null,
                    After = op is TipoOperacao.Insert or TipoOperacao.Update ? new Dictionary<string, object?>() : null
                };
                phase = op == TipoOperacao.Insert ? Phase.Set : Phase.Where;
                continue;
            }

            if (line.StartsWith("### SET", StringComparison.Ordinal))
            {
                phase = Phase.Set;
                continue;
            }

            if (line.StartsWith("### WHERE", StringComparison.Ordinal))
            {
                phase = Phase.Where;
                continue;
            }

            var col = RowColumnRegex.Match(line);
            if (col.Success && pending != null)
            {
                var key = "@" + col.Groups[1].Value;
                var rawVal = StripInlineComment(col.Groups[2].Value);
                var val = ParseSqlLiteral(rawVal);
                switch (phase)
                {
                    case Phase.Where:
                        pending.Before ??= new Dictionary<string, object?>();
                        pending.Before[key] = val;
                        break;
                    case Phase.Set:
                        pending.After ??= new Dictionary<string, object?>();
                        pending.After[key] = val;
                        break;
                }
            }
        }

        var last = TakePending(ref pending);
        if (last != null) yield return last;
    }

    private static BinlogParsedRowEvent? TakePending(ref Pending? pending)
    {
        if (pending == null) return null;
        var ev = BuildEvent(pending);
        pending = null;
        return ev;
    }

    private static BinlogParsedRowEvent? BuildEvent(Pending p)
    {
        JsonRowPayload? beforePayload = null;
        JsonRowPayload? afterPayload = null;
        if (p.Before is { Count: > 0 } b)
            beforePayload = ToPayload(b);
        if (p.After is { Count: > 0 } a)
            afterPayload = ToPayload(a);

        var sql = TryBuildSql(p.Operation, p.Database, p.Table, beforePayload, afterPayload);

        return new BinlogParsedRowEvent
        {
            BinlogFile = p.BinlogFile,
            BinlogPosition = p.Position,
            EventTimestamp = p.EventTime,
            Database = p.Database,
            Table = p.Table,
            Operation = p.Operation,
            Before = beforePayload,
            After = afterPayload,
            QuerySql = sql
        };
    }

    private static JsonRowPayload ToPayload(Dictionary<string, object?> map)
    {
        var keys = map.Keys.OrderBy(k => int.Parse(k.TrimStart('@'), CultureInfo.InvariantCulture)).ToList();
        var payload = new JsonRowPayload { Columns = keys };
        foreach (var k in keys)
            payload.Values[k] = map[k];
        return payload;
    }

    private static string? TryBuildSql(
        TipoOperacao op,
        string db,
        string tbl,
        JsonRowPayload? before,
        JsonRowPayload? after)
    {
        var qdb = "`" + db.Replace("`", "``") + "`";
        var qtbl = "`" + tbl.Replace("`", "``") + "`";
        var full = $"{qdb}.{qtbl}";

        try
        {
            return op switch
            {
                TipoOperacao.Insert when after != null =>
                    $"INSERT INTO {full} VALUES ({string.Join(", ", OrderedValues(after))});",
                TipoOperacao.Delete when before != null =>
                    $"DELETE FROM {full} WHERE {BuildEqPairs(before)};",
                TipoOperacao.Update when before != null && after != null =>
                    $"UPDATE {full} SET {BuildSetPairs(before, after)} WHERE {BuildEqPairs(before)};",
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> OrderedValues(JsonRowPayload p)
    {
        foreach (var c in p.Columns)
        {
            p.Values.TryGetValue(c, out var v);
            yield return FormatSqlLiteral(v);
        }
    }

    private static string BuildEqPairs(JsonRowPayload p)
    {
        var parts = new List<string>();
        foreach (var c in p.Columns)
        {
            p.Values.TryGetValue(c, out var v);
            parts.Add($"{c} = {FormatSqlLiteral(v)}");
        }

        return string.Join(" AND ", parts);
    }

    private static string BuildSetPairs(JsonRowPayload before, JsonRowPayload after)
    {
        var parts = new List<string>();
        foreach (var c in after.Columns)
        {
            after.Values.TryGetValue(c, out var av);
            before.Values.TryGetValue(c, out var bv);
            if (!Equals(av, bv))
                parts.Add($"{c} = {FormatSqlLiteral(av)}");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "/* sem alteração de colunas */";
    }

    private static string FormatSqlLiteral(object? v)
    {
        if (v == null) return "NULL";
        if (v is bool b) return b ? "1" : "0";
        if (v is string s) return "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";
        if (v is byte or sbyte or short or ushort or int or uint or long or ulong or decimal)
            return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "NULL";
        if (v is float or double) return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "NULL";
        if (v is DateTime dt)
            return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "'";
        if (v is byte[] bytes) return "0x" + BytesToHex(bytes);
        return "'" + v.ToString()?.Replace("'", "''") + "'";
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var x in bytes)
            sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static string StripInlineComment(string value)
    {
        var idx = value.IndexOf("/*", StringComparison.Ordinal);
        return idx < 0 ? value.Trim() : value.Substring(0, idx).Trim();
    }

    private static object? ParseSqlLiteral(string raw)
    {
        if (raw.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (raw.Length >= 2 && raw[0] == '\'' && raw[raw.Length - 1] == '\'')
        {
            var inner = raw.Substring(1, raw.Length - 2).Replace("''", "'");
            return inner;
        }

        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && raw.Length % 2 == 0)
        {
            try
            {
                var hex = raw.Substring(2);
                if (hex.Length == 0) return Array.Empty<byte>();
                return (object?)ParseHexBytes(hex) ?? raw;
            }
            catch
            {
                return raw;
            }
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;

        return raw;
    }

    private static byte[]? ParseHexBytes(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static bool TryParseBinlogTimestamp(string yyMMdd, string hms, out DateTime dt)
    {
        dt = default;
        if (yyMMdd.Length != 6) return false;
        if (!int.TryParse(yyMMdd.Substring(0, 2), out var yy)) return false;
        if (!int.TryParse(yyMMdd.Substring(2, 2), out var mm)) return false;
        if (!int.TryParse(yyMMdd.Substring(4, 2), out var dd)) return false;
        var year = yy >= 70 ? 1900 + yy : 2000 + yy;
        var parts = hms.Split(':');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], out var hh)) return false;
        if (!int.TryParse(parts[1], out var mi)) return false;
        if (!int.TryParse(parts[2], out var ss)) return false;
        try
        {
            dt = new DateTime(year, mm, dd, hh, mi, ss, DateTimeKind.Unspecified);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum Phase { Idle, Where, Set }

    private sealed class Pending
    {
        public required string BinlogFile { get; init; }
        public long Position { get; init; }
        public DateTime? EventTime { get; init; }
        public required string Database { get; init; }
        public required string Table { get; init; }
        public TipoOperacao Operation { get; init; }
        public Dictionary<string, object?>? Before { get; set; }
        public Dictionary<string, object?>? After { get; set; }
    }
}
