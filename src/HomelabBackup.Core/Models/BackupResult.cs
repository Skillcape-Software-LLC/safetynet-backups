namespace HomelabBackup.Core.Models;

public record BackupResult(
    bool Success,
    string SourceName,
    string? ArchiveFileName,
    TimeSpan Duration,
    int FilesCount,
    long UncompressedBytes,
    long CompressedBytes,
    bool VerificationPassed,
    int RetryCount,
    string? ErrorMessage);
