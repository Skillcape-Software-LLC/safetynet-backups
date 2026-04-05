using System.Threading.Channels;

namespace HomelabBackup.Web.Services;

public enum BackupJobType { Backup, Restore }

public record BackupJob(
    Guid JobId,
    BackupJobType Type,
    string? SourceName,
    bool DryRun,
    string? ArchiveFileName = null,
    string? DestinationPath = null,
    int? DestinationId = null,
    CancellationTokenSource? Cts = null);

public sealed class BackupJobQueue
{
    private readonly Channel<BackupJob> _channel = Channel.CreateBounded<BackupJob>(
        new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait });

    public ChannelReader<BackupJob> Reader => _channel.Reader;

    public (Guid JobId, bool Queued) EnqueueBackup(string? sourceName, bool dryRun, BackupStateService state)
    {
        // Prevent duplicate backups for the same source
        if (sourceName is not null &&
            state.ActiveJobs.Values.Any(j => j.Type == BackupJobType.Backup
                && string.Equals(j.SourceName, sourceName, StringComparison.OrdinalIgnoreCase)))
        {
            return (Guid.Empty, false);
        }

        var job = new BackupJob(Guid.NewGuid(), BackupJobType.Backup, sourceName, dryRun,
            Cts: new CancellationTokenSource());
        var queued = _channel.Writer.TryWrite(job);
        if (!queued) job.Cts?.Dispose();
        return (job.JobId, queued);
    }

    public (Guid JobId, bool Queued) EnqueueRestore(string archiveFileName, string destinationPath, int? destinationId = null)
    {
        var job = new BackupJob(Guid.NewGuid(), BackupJobType.Restore, null, false,
            ArchiveFileName: archiveFileName, DestinationPath: destinationPath,
            DestinationId: destinationId,
            Cts: new CancellationTokenSource());
        var queued = _channel.Writer.TryWrite(job);
        if (!queued) job.Cts?.Dispose();
        return (job.JobId, queued);
    }

    public bool TryCancelJob(Guid jobId, BackupStateService state)
    {
        if (state.ActiveJobs.TryGetValue(jobId, out var job) && job.Cts is not null)
        {
            job.Cts.Cancel();
            return true;
        }
        return false;
    }
}
