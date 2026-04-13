using System.IO;
using System.Reflection;
using MesasLog.Infrastructure.Data;
using Microsoft.Extensions.Configuration;

namespace MesasLog.Wpf;

internal static class BundledAppFiles
{
    internal const string EmbeddedAppSettings = "MesasLog.Wpf.appsettings.json";
    internal const string EmbeddedMesasSql = "MesasLog.Wpf.mesas.sql";

    internal static void AddEmbeddedAppSettings(IConfigurationBuilder cfg, Assembly assembly)
    {
        if (assembly.GetManifestResourceStream(EmbeddedAppSettings) is not { } embedded)
            return;
        using (embedded)
        {
            var ms = new MemoryStream();
            embedded.CopyTo(ms);
            ms.Position = 0;
            cfg.AddJsonStream(ms);
        }
    }

    internal static async Task ApplyMesasSnapshotAsync(SchemaInitializer schema, CancellationToken ct = default)
    {
        var mesasPath = Path.Combine(AppContext.BaseDirectory, "mesas.sql");
        if (File.Exists(mesasPath))
        {
            await schema.ApplyMesasSnapshotAsync(mesasPath, ct);
            return;
        }

        var asm = typeof(BundledAppFiles).Assembly;
        await using var stream = asm.GetManifestResourceStream(EmbeddedMesasSql);
        if (stream == null)
            return;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);
        await schema.ApplyMesasSnapshotFromContentAsync(sql, ct);
    }
}
