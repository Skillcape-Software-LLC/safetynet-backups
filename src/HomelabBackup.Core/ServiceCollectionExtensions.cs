using HomelabBackup.Core.Config;
using HomelabBackup.Core.Data;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomelabBackupCore(this IServiceCollection services, BackupConfig config)
    {
        // Config as singleton (refreshed in-place when UI saves)
        services.AddSingleton(config);
        services.AddSingleton(config.Ssh);

        // Infrastructure — transient SFTP (new connection per operation)
        services.AddTransient<ISftpService, SftpService>();
        services.AddSingleton<IArchiveService, ArchiveService>();

        // Services
        services.AddSingleton<IManifestService, ManifestService>();

        // Engines
        services.AddTransient<IBackupEngine, BackupEngine>();
        services.AddTransient<IRestoreEngine, RestoreEngine>();
        services.AddTransient<IRetentionPolicy, RetentionPolicy>();

        // Scheduler — always registered; handles null cron by polling until one is configured
        services.AddSingleton<SchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());

        return services;
    }
}
