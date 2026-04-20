using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MesasLog.Infrastructure.Binlog;

public sealed class MysqlBinlogExecutor
{
    private readonly ILogger<MysqlBinlogExecutor> _logger;

    public MysqlBinlogExecutor(ILogger<MysqlBinlogExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<MysqlBinlogResult> RunAsync(
        string mysqlBinlogPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mysqlBinlogPath,
            Arguments = JoinProcessArguments(arguments),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) stdout.WriteLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderr.WriteLine(e.Data);
        };

        try
        {
            if (!proc.Start())
                return new MysqlBinlogResult(false, "", "Falha ao iniciar processo mysqlbinlog.", -1);

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                await WaitForProcessExitAsync(proc, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill();
                        _logger.LogWarning("mysqlbinlog encerrado por timeout ({Timeout}s).", timeout.TotalSeconds);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao finalizar mysqlbinlog após timeout.");
                }

                return new MysqlBinlogResult(false, stdout.ToString(), stderr + Environment.NewLine + "Timeout.", -2);
            }

            var err = stderr.ToString();
            var @out = stdout.ToString();
            if (proc.ExitCode != 0)
            {
                _logger.LogError("mysqlbinlog saiu com código {Code}. Stderr: {Err}", proc.ExitCode, err);
                return new MysqlBinlogResult(false, @out, err, proc.ExitCode);
            }

            return new MysqlBinlogResult(true, @out, err, proc.ExitCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao executar mysqlbinlog.");
            return new MysqlBinlogResult(false, "", ex.Message, -3);
        }
    }

    private static string JoinProcessArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(EscapeProcessArgument(arguments[i]));
        }

        return sb.ToString();
    }

    private static string EscapeProcessArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static async Task WaitForProcessExitAsync(Process process, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object>();
        void OnExited(object? sender, EventArgs e)
        {
            process.Exited -= OnExited;
            tcs.TrySetResult(null!);
        }

        process.EnableRaisingEvents = true;
        process.Exited += OnExited;
        if (process.HasExited)
        {
            process.Exited -= OnExited;
            return;
        }

        using (cancellationToken.Register(() =>
               {
                   process.Exited -= OnExited;
                   tcs.TrySetCanceled();
               }))
        {
            await tcs.Task.ConfigureAwait(false);
        }
    }
}

public readonly struct MysqlBinlogResult
{
    public MysqlBinlogResult(bool success, string standardOutput, string standardError, int exitCode)
    {
        Success = success;
        StandardOutput = standardOutput;
        StandardError = standardError;
        ExitCode = exitCode;
    }

    public bool Success { get; }
    public string StandardOutput { get; }
    public string StandardError { get; }
    public int ExitCode { get; }
}
