using Cronos;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Services;

public sealed class SchedulerService : IHostedService, IDisposable
{
    private readonly BackupConfig _config;
    private readonly IBackupEngine _backupEngine;
    private readonly IRetentionPolicy _retentionPolicy;
    private readonly ILogger<SchedulerService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _runningTask;

    public DateTime? NextRunUtc { get; private set; }

    /// <summary>
    /// Optional factory for creating progress reporters per source. Set by the Web host to relay progress to the UI.
    /// </summary>
    public Func<string, IProgress<BackupProgressEvent>>? ProgressFactory { get; set; }

    /// <summary>
    /// Optional callback invoked when a scheduled backup completes. Set by the Web host for UI updates.
    /// </summary>
    public Action<BackupResult>? OnResultCompleted { get; set; }

    public SchedulerService(
        BackupConfig config,
        IBackupEngine backupEngine,
        IRetentionPolicy retentionPolicy,
        ILogger<SchedulerService> logger)
    {
        _config = config;
        _backupEngine = backupEngine;
        _retentionPolicy = retentionPolicy;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Schedule?.Cron is null)
        {
            _logger.LogInformation("No schedule configured — running in one-shot mode");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runningTask = RunScheduleLoopAsync(_cts.Token);
        _logger.LogInformation("Scheduler started with cron: {Cron}", _config.Schedule.Cron);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();

            if (_runningTask is not null)
            {
                try { await _runningTask; }
                catch (OperationCanceledException) { }
            }
        }

        _logger.LogInformation("Scheduler stopped");
    }

    private async Task RunScheduleLoopAsync(CancellationToken ct)
    {
        var cron = CronExpression.Parse(_config.Schedule!.Cron);

        while (!ct.IsCancellationRequested)
        {
            var next = cron.GetNextOccurrence(DateTime.UtcNow);
            if (next is null)
            {
                _logger.LogWarning("No next occurrence found for cron expression");
                break;
            }

            NextRunUtc = next;
            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation("Next backup scheduled at {NextRun} UTC (in {Delay})", next, delay);
                await Task.Delay(delay, ct);
            }

            if (ct.IsCancellationRequested)
                break;

            _logger.LogInformation("Scheduled backup starting...");

            foreach (var source in _config.Sources)
            {
                try
                {
                    var progress = ProgressFactory?.Invoke(source.Name);
                    var result = await _backupEngine.RunAsync(
                        source, _config.Destination, _config.Compression,
                        dryRun: false, progress: progress, ct: ct);

                    OnResultCompleted?.Invoke(result);

                    if (result.Success)
                        _logger.LogInformation("Scheduled backup for {SourceName} completed successfully", source.Name);
                    else
                        _logger.LogError("Scheduled backup for {SourceName} failed: {Error}", source.Name, result.ErrorMessage);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled backup for {SourceName} threw an exception", source.Name);
                }
            }

            // Run retention after all backups
            try
            {
                var sourceNames = _config.Sources.Select(s => s.Name).ToList();
                await _retentionPolicy.ApplyAsync(
                    _config.Destination, _config.Retention, sourceNames,
                    dryRun: false, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled retention policy failed");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
