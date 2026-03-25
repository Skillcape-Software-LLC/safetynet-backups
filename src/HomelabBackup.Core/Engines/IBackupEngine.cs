using HomelabBackup.Core.Config;
using HomelabBackup.Core.Infrastructure;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Engines;

public interface IBackupEngine
{
    Task<BackupResult> RunAsync(
        SourceConfig source,
        DestinationConfig destination,
        ITransferService transfer,
        string compression,
        bool dryRun,
        IProgress<BackupProgressEvent>? progress = null,
        SemaphoreSlim? compressionSemaphore = null,
        CancellationToken ct = default);
}
