using HomelabBackup.Core.Config;
using HomelabBackup.Core.Models;

namespace HomelabBackup.Core.Engines;

public interface IRestoreEngine
{
    Task<BackupResult> RestoreLatestAsync(
        string sourceName,
        DestinationConfig remoteDestination,
        string localDestination,
        CancellationToken ct = default);

    Task<BackupResult> RestoreFileAsync(
        string archiveFileName,
        DestinationConfig remoteDestination,
        string localDestination,
        CancellationToken ct = default);
}
