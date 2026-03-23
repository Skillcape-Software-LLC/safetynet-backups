namespace HomelabBackup.Core.Models;

public record BackupProgressEvent(
    string Source,
    string CurrentFile,
    int FilesProcessed,
    int FilesTotal,
    long BytesProcessed,
    BackupPhase Phase);
