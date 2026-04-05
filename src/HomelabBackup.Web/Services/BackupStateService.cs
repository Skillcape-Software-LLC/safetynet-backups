using System.Collections.Concurrent;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Web.Services;

public sealed class BackupStateService
{
    public event Action<BackupProgressEvent>? OnProgressChanged;
    public event Action<BackupResult>? OnBackupCompleted;
    public event Action<LogEntry>? OnLogMessage;

    public ConcurrentDictionary<string, BackupResult> LastResults { get; } = new();
    public ConcurrentDictionary<Guid, BackupJob> ActiveJobs { get; } = new();
    public ConcurrentDictionary<string, BackupProgressEvent> LatestProgress { get; } = new();

    public void ReportProgress(BackupProgressEvent evt)
    {
        LatestProgress[evt.Source] = evt;
        OnProgressChanged?.Invoke(evt);
    }

    public IProgress<BackupProgressEvent> CreateProgress(string sourceName)
    {
        return new Progress<BackupProgressEvent>(evt => ReportProgress(evt));
    }

    public void ReportCompletion(BackupResult result)
    {
        LatestProgress.TryRemove(result.SourceName, out _);
        LastResults[result.SourceName] = result;
        OnBackupCompleted?.Invoke(result);
    }

    public void ReportLog(LogEntry entry)
    {
        OnLogMessage?.Invoke(entry);
    }
}

public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Source,
    string Message);
