using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HomelabBackup.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHomelabBackupCore(this IServiceCollection services, BackupConfig config)
    {
        // Config as singleton
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

        // Scheduler (only if cron is configured)
        if (config.Schedule?.Cron is not null)
        {
            services.AddSingleton<SchedulerService>();
            services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        }

        return services;
    }
}
