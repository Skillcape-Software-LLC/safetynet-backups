using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Engines;

public record RetentionResult(
    IReadOnlyList<BackupEntry> DeletedArchives,
    IReadOnlyList<BackupEntry> RetainedArchives);
