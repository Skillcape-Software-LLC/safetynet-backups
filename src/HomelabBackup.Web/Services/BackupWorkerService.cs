using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Web.Services;

public sealed class BackupWorkerService : BackgroundService
{
    private readonly BackupJobQueue _queue;
    private readonly BackupStateService _state;
    private readonly IServiceProvider _services;
    private readonly ILogger<BackupWorkerService> _logger;

    public BackupWorkerService(
        BackupJobQueue queue,
        BackupStateService state,
        IServiceProvider services,
        ILogger<BackupWorkerService> logger)
    {
        _queue = queue;
        _state = state;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupWorkerService started — waiting for jobs");

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            _state.ActiveJobs[job.JobId] = job;

            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken, job.Cts?.Token ?? CancellationToken.None);

                switch (job.Type)
                {
                    case BackupJobType.Backup:
                        await HandleBackupAsync(job, linkedCts.Token);
                        break;
                    case BackupJobType.Restore:
                        await HandleRestoreAsync(job, linkedCts.Token);
                        break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("BackupWorkerService stopping");
                break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Job {JobId} was cancelled", job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing job {JobId}", job.JobId);
            }
            finally
            {
                _state.ActiveJobs.TryRemove(job.JobId, out _);
                job.Cts?.Dispose();
            }
        }
    }

    private async Task HandleBackupAsync(BackupJob job, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IBackupEngine>();
        var config = scope.ServiceProvider.GetRequiredService<BackupConfig>();

        var sources = config.Sources.AsEnumerable();
        if (job.SourceName is not null)
            sources = sources.Where(s => s.Name.Equals(job.SourceName, StringComparison.OrdinalIgnoreCase));

        foreach (var source in sources.ToList())
        {
            _logger.LogInformation("Starting backup for {SourceName} (JobId: {JobId})", source.Name, job.JobId);
            var progress = _state.CreateProgress(source.Name);

            var result = await engine.RunAsync(
                source, config.Destination, config.Compression,
                job.DryRun, progress, ct);

            _state.ReportCompletion(result);

            if (result.Success)
                _logger.LogInformation("Backup completed for {SourceName}: {ArchiveFileName}", source.Name, result.ArchiveFileName);
            else
                _logger.LogError("Backup failed for {SourceName}: {Error}", source.Name, result.ErrorMessage);
        }
    }

    private async Task HandleRestoreAsync(BackupJob job, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IRestoreEngine>();
        var config = scope.ServiceProvider.GetRequiredService<BackupConfig>();

        _logger.LogInformation("Starting restore of {ArchiveFileName} (JobId: {JobId})", job.ArchiveFileName, job.JobId);

        var result = await engine.RestoreFileAsync(
            job.ArchiveFileName!, config.Destination, job.DestinationPath!, ct);

        _state.ReportCompletion(result);

        if (result.Success)
            _logger.LogInformation("Restore completed: {ArchiveFileName}", job.ArchiveFileName);
        else
            _logger.LogError("Restore failed: {Error}", result.ErrorMessage);
    }
}
