namespace HomelabBackup.Core.Models;

public record BackupEntry(
    string SourceName,
    string ArchiveFileName,
    DateTime CreatedUtc,
    long CompressedBytes,
    int FileCount = 0);
