using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MesasLog.Infrastructure.Binlog;

public sealed class MysqlBinlogExecutor(ILogger<MysqlBinlogExecutor> logger)
{
    public async Task<MysqlBinlogResult> RunAsync(
        string mysqlBinlogPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mysqlBinlogPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in arguments)
            psi.ArgumentList.Add(a);

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
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                        logger.LogWarning("mysqlbinlog encerrado por timeout ({Timeout}s).", timeout.TotalSeconds);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Erro ao finalizar mysqlbinlog após timeout.");
                }

                return new MysqlBinlogResult(false, stdout.ToString(), stderr + Environment.NewLine + "Timeout.", -2);
            }

            var err = stderr.ToString();
            var @out = stdout.ToString();
            if (proc.ExitCode != 0)
            {
                logger.LogError("mysqlbinlog saiu com código {Code}. Stderr: {Err}", proc.ExitCode, err);
                return new MysqlBinlogResult(false, @out, err, proc.ExitCode);
            }

            return new MysqlBinlogResult(true, @out, err, proc.ExitCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao executar mysqlbinlog.");
            return new MysqlBinlogResult(false, "", ex.Message, -3);
        }
    }
}

public readonly record struct MysqlBinlogResult(bool Success, string StandardOutput, string StandardError, int ExitCode);
