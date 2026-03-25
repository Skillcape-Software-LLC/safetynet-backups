using Cronos;
using HomelabBackup.Core.Config;
using HomelabBackup.Core.Engines;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HomelabBackup.Core.Services;

public sealed class SchedulerService : IHostedService, IDisposable
{
    private readonly BackupConfig _config;
    private readonly IBackupEngine _backupEngine;
    private readonly IRetentionPolicy _retentionPolicy;
    private readonly TransferServiceFactory _transferFactory;
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
        TransferServiceFactory transferFactory,
        ILogger<SchedulerService> logger)
    {
        _config = config;
        _backupEngine = backupEngine;
        _retentionPolicy = retentionPolicy;
        _transferFactory = transferFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runningTask = RunScheduleLoopAsync(_cts.Token);
        _logger.LogInformation("Scheduler started (cron: {Cron})", _config.Schedule?.Cron ?? "none");
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
        while (!ct.IsCancellationRequested)
        {
            var cron = _config.Schedule?.Cron;

            // No cron configured — poll every 30 seconds waiting for one to be set via the UI
            if (string.IsNullOrWhiteSpace(cron))
            {
                NextRunUtc = null;
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                continue;
            }

            CronExpression cronExpr;
            try
            {
                cronExpr = CronExpression.Parse(cron);
            }
            catch (CronFormatException ex)
            {
                _logger.LogWarning("Invalid cron expression '{Cron}': {Error} — retrying in 30s", cron, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                continue;
            }

            var next = cronExpr.GetNextOccurrence(DateTime.UtcNow);
            if (next is null)
            {
                _logger.LogWarning("No next occurrence found for cron '{Cron}' — retrying in 60s", cron);
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
                continue;
            }

            NextRunUtc = next;
            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                _logger.LogInformation("Next backup scheduled at {NextRun} UTC (in {Delay})", next, delay);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested)
                break;

            // Re-check cron in case it was cleared while we waited
            if (string.IsNullOrWhiteSpace(_config.Schedule?.Cron))
                continue;

            _logger.LogInformation("Scheduled backup starting...");

            foreach (var source in _config.Sources)
            {
                var destination = ResolveDestination(source);
                if (destination is null)
                {
                    _logger.LogWarning("No destination configured for source '{SourceName}' — skipping", source.Name);
                    continue;
                }

                try
                {
                    using var transfer = _transferFactory.Create(destination);
                    var progress = ProgressFactory?.Invoke(source.Name);
                    var result = await _backupEngine.RunAsync(
                        source, destination, transfer, _config.Compression,
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

            // Run retention per destination group after all backups
            var sourcesByDestination = _config.Sources
                .GroupBy(s => s.DestinationId ?? _config.Destinations.FirstOrDefault()?.Id)
                .Where(g => g.Key.HasValue);

            foreach (var group in sourcesByDestination)
            {
                var dest = _config.Destinations.FirstOrDefault(d => d.Id == group.Key);
                if (dest is null) continue;

                try
                {
                    using var transfer = _transferFactory.Create(dest);
                    var sourceNames = group.Select(s => s.Name).ToList();
                    await _retentionPolicy.ApplyAsync(dest, transfer, _config.Retention, sourceNames, dryRun: false, ct: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled retention policy failed for destination '{DestName}'", dest.Name);
                }
            }
        }
    }

    private DestinationConfig? ResolveDestination(SourceConfig source)
    {
        if (source.DestinationId.HasValue)
            return _config.Destinations.FirstOrDefault(d => d.Id == source.DestinationId.Value);
        return _config.Destinations.FirstOrDefault();
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }
}
