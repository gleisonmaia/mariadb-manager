using MesasLog.Application.Services;
using MesasLog.Application.Validation;
using MesasLog.Infrastructure.Binlog;
using MesasLog.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace MesasLog.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMesasLogServices(this IServiceCollection services)
    {
        services.AddSingleton<MariaDbConnectionFactory>();
        services.AddSingleton<SchemaInitializer>();
        services.AddSingleton<CheckpointRepository>();
        services.AddSingleton<BinlogEventRepository>();
        services.AddSingleton<MysqlBinlogPathResolver>();
        services.AddSingleton<MysqlBinlogExecutor>();
        services.AddSingleton<BinlogVerboseRowParser>();
        services.AddSingleton<RowImageValidator>();
        services.AddSingleton<BinlogIngestionService>();
        services.AddSingleton<DataReplayService>();
        services.AddSingleton<DatabaseBackupRestoreService>();
        return services;
    }
}
