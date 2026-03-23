using HomelabBackup.Core.Config;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Engines;

public interface IBackupEngine
{
    Task<BackupResult> RunAsync(
        SourceConfig source,
        DestinationConfig destination,
        string compression,
        bool dryRun,
        IProgress<BackupProgressEvent>? progress = null,
        CancellationToken ct = default);
}
